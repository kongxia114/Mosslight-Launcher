using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mosslight.Core.Logging;

namespace Mosslight.Core.Services.Engine;

// 远程版本清单：拉取 / 缓存 / 分组（正式版、预览版、远古版、愚人节版）。
// 缓存 6 小时；过期后拉取失败就用旧缓存并标记 Stale，缓存损坏则重新拉。
// ⚠️ 清单没有「愚人节」类型，只能靠名单认；分组时愚人节必须优先于 snapshot 判断。

/// <summary>清单里的一个版本条目。</summary>
public sealed record ManifestEntry(
    string Id,
    string DisplayName,
    string Type,
    string ReleaseTime,
    string Url);

/// <summary>清单分组的结果。</summary>
public sealed record GroupedVersions(
    ManifestEntry? LatestRelease,
    ManifestEntry? LatestSnapshot,
    IReadOnlyList<(string Name, List<ManifestEntry> Items)> Groups,
    IReadOnlyList<string> MissingAprilFools);

/// <summary>拿清单的结果说明。</summary>
public sealed record ManifestInfo(
    string? Source,      // cache / official / bmclapi / null
    bool Stale,
    double? Age,
    string Error);

/// <summary>远程版本清单。</summary>
public sealed class VersionManifest
{
    private static readonly (string Name, string Url)[] ManifestUrls =
    {
        ("official", "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"),
        ("bmclapi", "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json"),
    };

    private const string CacheName = "manifest_cache.json";

    // 缓存多久算过期（秒）。
    private const double CacheMaxAge = 6 * 3600;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private const string UserAgent = "Mosslight/0.5 (+https://github.com/kongxia114/Mosslight-Launcher)";

    // 愚人节版本名单：(清单里的 id, 年份)。
    private static readonly (string Id, int Year)[] AprilFools =
    {
        ("26w14a", 2026),
        ("25w14craftmine", 2025),
        ("24w14potato", 2024),
        ("23w13a_or_b", 2023),
        ("22w13oneblockatatime", 2022),
        ("20w14infinite", 2020),
        ("3D Shareware v1.34", 2019),
        ("1.RV-Pre1", 2016),
        ("15w14a", 2015),
        ("2.0", 2013),          // 官方清单里没有，只有 PCL 这类启动器提供
    };

    // 显示名和清单 id 不一样的（就这一个）。
    private static readonly Dictionary<string, string> AprilFoolsAlias = new(StringComparer.Ordinal)
    {
        ["20w14infinite"] = "20w14∞",
    };

    /// <summary>分组顺序 = 界面上的顺序。</summary>
    public static readonly string[] GroupOrder = { "正式版", "预览版", "远古版", "愚人节版" };

    private const string GroupRelease = "正式版";
    private const string GroupSnapshot = "预览版";
    private const string GroupOld = "远古版";
    private const string GroupApril = "愚人节版";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    private readonly string _cachePath;

    public VersionManifest(string configDir)
    {
        _cachePath = Path.Combine(configDir, CacheName);
    }

    /// <summary>{id: 年份}，给界面显示"2025 | …"那种前缀用。</summary>
    public static IReadOnlyDictionary<string, int> AprilFoolsYears()
        => AprilFools.ToDictionary(x => x.Id, x => x.Year, StringComparer.Ordinal);

    // 拉取

    /// <summary>只取清单的原始 JSON（安装单个版本时要用它的 url）。</summary>
    public async Task<JsonObject?> LoadRawAsync(bool force = false, CancellationToken token = default)
    {
        var (data, _) = await LoadAsync(force, token).ConfigureAwait(false);
        return data;
    }

    public async Task<(JsonObject? Data, ManifestInfo Info)> LoadAsync(
        bool force = false,
        CancellationToken token = default)
    {
        var cached = ReadCache();
        double? age = null;

        if (cached is not null)
        {
            var fetchedAt = cached["fetched_at"]?.GetValue<double>() ?? 0;
            age = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - fetchedAt);
            // ⚠️ 缓存还得**过体检**：类型认不出来的缓存直接当没有，逼它去重新拉。
            // 不然一份坏清单被写进 manifest_cache.json 之后，之后每次都会拿它出来，
            // 界面永远停在"共 919 个版本、却只列出 60 条"那种半空状态。
            if (!force && age < CacheMaxAge && UnclassifiedTypes(cached).Count == 0)
            {
                return (cached, new ManifestInfo("cache", false, age, string.Empty));
            }
        }

        var lastError = string.Empty;
        foreach (var (name, url) in ManifestUrls)
        {
            try
            {
                var data = await FetchAsync(url, token).ConfigureAwait(false);

                // ⚠️ 先把这次拉回来的清单**体检**一遍再去用。
                // 类型认不出来的条目会被界面静默丢掉，结果是"共 N 个版本"旁边
                // 只剩几条、预览版整组空白。
                // 只要有干净的缓存就退回缓存 —— 宁可提示"这是缓存"，也不要摆一个坏列表。
                var unknown = UnclassifiedTypes(data);
                if (unknown.Count > 0)
                {
                    var detail = string.Join("、", unknown.Select(kv => $"{kv.Key}×{kv.Value}"));
                    var message = $"新清单里有 {unknown.Values.Sum()} 条 type 认不出来（{detail}）";
                    LogBus.Write($"[Manifest] {name}：{message}", LogLevel.Warn);

                    if (cached is not null && UnclassifiedTypes(cached).Count == 0)
                    {
                        // 缓存是好的：用它，并且**不把坏数据写回缓存**（否则下次更糟）
                        return (cached, new ManifestInfo("cache", true, age, message));
                    }
                }

                WriteCache(data);
                return (data, new ManifestInfo(name, false, 0, string.Empty));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                          or JsonException or InvalidOperationException
                                          or NotSupportedException)
            {
                lastError = $"{name}: {ex.Message}";
            }
        }

        // 全挂了：有旧缓存就用旧的（断网时至少还能看列表）
        if (cached is not null)
        {
            return (cached, new ManifestInfo("cache", true, age, lastError));
        }
        return (null, new ManifestInfo(null, false, null, lastError));
    }

    private static async Task<JsonObject> FetchAsync(string url, CancellationToken token)
    {
        var bytes = await Http.GetByteArrayAsync(url, token).ConfigureAwait(false);
        var data = JsonIO.ParseBytes(bytes) as JsonObject
                   ?? throw new JsonException("清单格式不对（不是 JSON 对象）");
        if (data["versions"] is not JsonArray)
        {
            throw new JsonException("清单格式不对（没有 versions 字段）");
        }
        return data;
    }

    public static IReadOnlyDictionary<string, int> UnclassifiedTypes(JsonObject data)
    {
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "release", "snapshot", "old_beta", "old_alpha",
        };

        var unknown = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in (data["versions"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            string type;
            try
            {
                type = item["type"]?.GetValue<string>() ?? string.Empty;
            }
            catch (InvalidOperationException)
            {
                // type 不是字符串（数字 / 对象）：也算"认不出来"，但不能让它把整次解析带崩
                type = "<非字符串>";
            }

            if (known.Contains(type))
            {
                continue;
            }

            var key = type.Length == 0 ? "<空>" : type;
            unknown[key] = unknown.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        return unknown;
    }

    private JsonObject? ReadCache()
    {
        try
        {
            var root = JsonIO.ParseBytes(File.ReadAllBytes(_cachePath)) as JsonObject;
            return root?["versions"] is JsonArray ? root : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
                                      or NotSupportedException)
        {
            return null;
        }
    }

    private void WriteCache(JsonObject data)
    {
        var payload = (JsonObject)data.DeepClone();
        payload["fetched_at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var tmp = _cachePath + ".tmp";
            File.WriteAllText(tmp, payload.ToJsonString(new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }), new UTF8Encoding(false));
            File.Move(tmp, _cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"[Manifest] 缓存写不进去（{ex.Message}），下次还得重拉");
        }
    }

    // 分组

    private static ManifestEntry Entry(JsonObject item)
    {
        var id = item["id"]?.GetValue<string>() ?? string.Empty;
        return new ManifestEntry(
            Id: id,
            DisplayName: AprilFoolsAlias.TryGetValue(id, out var alias) ? alias : id,
            Type: item["type"]?.GetValue<string>() ?? string.Empty,
            ReleaseTime: item["releaseTime"]?.GetValue<string>() ?? string.Empty,
            Url: item["url"]?.GetValue<string>() ?? string.Empty);
    }

    /// <summary>把清单分成界面上那四组 + 最新两个。</summary>
    public static GroupedVersions Group(JsonObject data)
    {
        var versions = (data["versions"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>()
            .ToList();

        var april = AprilFools.ToDictionary(x => x.Id, x => x.Year, StringComparer.Ordinal);
        var groups = GroupOrder.ToDictionary(name => name, _ => new List<ManifestEntry>(), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in versions)
        {
            var id = item["id"]?.GetValue<string>() ?? string.Empty;
            if (id.Length == 0 || !seen.Add(id))
            {
                continue;
            }

            var entry = Entry(item);

            // ⚠️ 愚人节**最先判**：它们大多是 snapshot，先判 snapshot 会被算进"预览版"
            if (april.ContainsKey(id))
            {
                groups[GroupApril].Add(entry);
                continue;
            }

            switch (entry.Type)
            {
                case "release":
                    groups[GroupRelease].Add(entry);
                    break;
                case "snapshot":
                    groups[GroupSnapshot].Add(entry);
                    break;
                case "old_beta":
                case "old_alpha":
                    groups[GroupOld].Add(entry);
                    break;
                // 别的 type（以后新增的）先不放进任何组，免得列表里冒出没归类的项
            }
        }

        // 清单里没有的愚人节版本（比如 2.0）单独报出来，界面可以标"官方清单里没有"
        var missing = AprilFools
            .Where(x => !seen.Contains(x.Id))
            .Select(x => x.Id)
            .ToList();

        ManifestEntry? Find(string id)
            => id.Length == 0
                ? null
                : versions.Where(v => v["id"]?.GetValue<string>() == id).Select(Entry).FirstOrDefault();

        var latest = data["latest"] as JsonObject;
        var latestRelease = Find(latest?["release"]?.GetValue<string>() ?? string.Empty);
        var latestSnapshot = Find(latest?["snapshot"]?.GetValue<string>() ?? string.Empty);

        return new GroupedVersions(
            latestRelease,
            latestSnapshot,
            GroupOrder.Select(name => (name, groups[name])).ToList(),
            missing);
    }

    /// <summary>一步到位：拿清单 + 分组。</summary>
    public async Task<(GroupedVersions? Grouped, ManifestInfo Info)> FetchGroupedAsync(
        bool force = false,
        CancellationToken token = default)
    {
        var (data, info) = await LoadAsync(force, token).ConfigureAwait(false);
        return data is null ? (null, info) : (Group(data), info);
    }

    /// <summary>清单里所有版本的 id（按清单顺序 = 新的在前）。</summary>
    public static List<string> AllVersionIds(JsonObject data)
        => (data["versions"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>()
            .Select(v => v["id"]?.GetValue<string>() ?? string.Empty)
            .Where(id => id.Length > 0)
            .ToList();
}
