using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mosslight.Core.Localization;

namespace Mosslight.Core.Services.Engine;

// 把版本 JSON + 资源索引变成下载任务和 natives 列表；只做规划，不联网、不写盘。
// 真正下载走 DownloadManager，解压走 NativesExtractor。

/// <summary>一个要下载的文件。</summary>
public sealed record InstallTask(
    string Url,
    string Path,
    string Sha1 = "",
    long Size = 0,
    string Label = "",
    /// <summary>界面上显示成 `[库] xxx.jar` 那种前缀。</summary>
    string Kind = "",
    /// <summary>备用地址（可选）：主地址挂了就试它们。</summary>
    IReadOnlyList<string>? AltUrls = null);

/// <summary>一个版本的安装计划。</summary>
public sealed class InstallPlan
{
    public string VersionId { get; init; } = string.Empty;

    public string VersionDir { get; init; } = string.Empty;

    public InstallTask? ClientJar { get; set; }

    public List<InstallTask> Libraries { get; } = new();

    public List<InstallTask> Assets { get; } = new();

    public InstallTask? AssetIndex { get; set; }

    public InstallTask? Logging { get; set; }

    /// <summary>有 native 的库（下载完由 NativesExtractor 解压）。</summary>
    public List<InstallTask> Natives { get; } = new();

    /// <summary>计划里需要、但 JSON 没给全的东西（调用方决定要不要拦）。</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>
    /// 全部下载任务（顺序：客户端 → 库 → natives → 资源索引 → 资源 → 日志）；⚠️
    /// **natives 一定要在里面**。
    /// </summary>
    public IEnumerable<InstallTask> Tasks()
    {
        if (ClientJar is not null)
        {
            yield return ClientJar;
        }
        foreach (var task in Libraries)
        {
            yield return task;
        }
        foreach (var task in Natives)
        {
            yield return task;
        }
        if (AssetIndex is not null)
        {
            yield return AssetIndex;
        }
        foreach (var task in Assets)
        {
            yield return task;
        }
        if (Logging is not null)
        {
            yield return Logging;
        }
    }

    public long TotalBytes() => Tasks().Sum(t => t.Size);
}

/// <summary>安装计划的编排。</summary>
public static class GameInstaller
{
    /// <summary>
    /// 资源文件（objects）的官方地址；镜像替换交给 DownloadManager 的 MirrorUrl。
    /// </summary>
    public const string ResourcesBase = "https://resources.download.minecraft.net";

    // 版本清单 / 版本 JSON / 资源索引的重试次数与退避。
    private const int JsonRetry = 2;

    private const double JsonBackoff = 0.6;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mosslight/0.5 (+https://github.com/kongxia114/Mosslight-Launcher-2)");
        return client;
    }

    // 版本 JSON 落盘

    public static string WriteVersionJson(JsonObject vj, string versionDir, string? jarName = null)
    {
        var name = !string.IsNullOrEmpty(jarName)
            ? jarName
            : vj["id"]?.GetValue<string>() ?? string.Empty;

        var data = (JsonObject)vj.DeepClone();
        if (name.Length > 0 && data["id"]?.GetValue<string>() != name)
        {
            data["id"] = name;
        }

        Directory.CreateDirectory(versionDir);
        var path = Path.Combine(versionDir, name + ".json");
        File.WriteAllText(path, data.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + "\n", new UTF8Encoding(false));
        return path;
    }

    // 取 JSON

    /// <summary>取一个 JSON（版本清单 / 版本 JSON / 资源索引都走它）。</summary>
    public static async Task<JsonObject?> HttpJsonAsync(string url, CancellationToken token = default)
    {
        var mirrored = DownloadManager.MirrorUrl(url);
        var candidates = mirrored == url ? new[] { url } : new[] { mirrored, url };

        Exception? last = null;
        for (var attempt = 0; attempt <= JsonRetry; attempt++)
        {
            foreach (var candidate in candidates)
            {
                try
                {
                    var bytes = await Http.GetByteArrayAsync(candidate, token).ConfigureAwait(false);
                    return JsonIO.ParseBytes(bytes) as JsonObject;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                              or JsonException or NotSupportedException)
                {
                    last = ex;
                }
            }
            if (attempt < JsonRetry)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(JsonBackoff * (attempt + 1)), token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }
        }

        System.Diagnostics.Debug.WriteLine($"[Install] 取不到 {url}：{last?.Message}");
        return null;
    }

    public static bool LooksLikeLauncherJson(JsonObject? vj)
    {
        if (vj is null || string.IsNullOrEmpty(vj["mainClass"]?.GetValue<string>()))
        {
            return false;
        }
        return vj["libraries"] is JsonArray { Count: > 0 }
               || vj["downloads"] is JsonObject
               || !string.IsNullOrEmpty(vj["inheritsFrom"]?.GetValue<string>());
    }

    /// <summary>拿资源索引 JSON：**本地有就用本地的**，没有才用 fetch 去下。</summary>
    public static async Task<JsonObject?> LoadAssetIndexAsync(
        string mcDir,
        JsonObject vj,
        Func<string, CancellationToken, Task<JsonObject?>>? fetch = null,
        CancellationToken token = default)
    {
        var meta = vj["assetIndex"] as JsonObject;
        var indexId = meta?["id"]?.GetValue<string>() ?? string.Empty;

        if (indexId.Length > 0)
        {
            var local = Path.Combine(mcDir, "assets", "indexes", indexId + ".json");
            try
            {
                if (File.Exists(local))
                {
                    return JsonIO.ParseBytes(File.ReadAllBytes(local)) as JsonObject;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
                                          or NotSupportedException)
            {
                // 坏了就走网络那份
            }
        }

        var url = meta?["url"]?.GetValue<string>();
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }
        try
        {
            return fetch is not null
                ? await fetch(url, token).ConfigureAwait(false)
                : await HttpJsonAsync(url, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    // 规划

    /// <summary>
    /// `group:artifact:version` → `group/path/artifact/version/artifact-version.jar`。
    /// </summary>
    private static string MavenToPath(string? name)
    {
        var parts = (name ?? string.Empty).Split(':');
        if (parts.Length < 3)
        {
            return string.Empty;
        }
        var group = parts[0];
        var artifact = parts[1];
        var version = parts[2];
        // 有的版本带了 classifier（`group:artifact:version:classifier`），文件名要带上
        var classifier = parts.Length >= 4 && parts[3].Length > 0 ? "-" + parts[3] : string.Empty;
        return $"{group.Replace('.', '/')}/{artifact}/{version}/{artifact}-{version}{classifier}.jar";
    }

    /// <summary>这个库在当前系统上要哪个 native（没有就返回空串）。</summary>
    private static string NativeKeyForThisOs(JsonObject lib)
    {
        if (lib["natives"] is not JsonObject natives)
        {
            return string.Empty;
        }

        var key = PlatformInfo.PlatformName switch
        {
            "windows" => "windows",
            "osx" => "osx",
            _ => "linux",
        };

        if (natives[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String)
        {
            return value.GetValue<string>();
        }
        // macOS 的两种写法
        if (key == "osx" && natives["macos"] is JsonValue macValue &&
            macValue.GetValueKind() == JsonValueKind.String)
        {
            return macValue.GetValue<string>();
        }
        return string.Empty;
    }

    /// <summary>算出一个版本的安装计划。</summary>
    public static InstallPlan PlanVersion(
        JsonObject vj,
        string mcDir,
        Func<JsonNode?, bool>? rulesAllow = null,
        JsonObject? assetIndex = null,
        string? jarName = null)
    {
        var versionId = vj["id"]?.GetValue<string>() ?? string.Empty;
        var versionDir = Path.Combine(mcDir, "versions", jarName ?? versionId);
        var allow = rulesAllow ?? (rules => LaunchRules.RulesAllow(rules));

        var plan = new InstallPlan { VersionId = versionId, VersionDir = versionDir };

        // 客户端 jar
        var client = (vj["downloads"] as JsonObject)?["client"] as JsonObject;
        var clientUrl = client?["url"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(clientUrl))
        {
            plan.ClientJar = new InstallTask(
                Url: clientUrl,
                Path: Path.Combine(versionDir, (jarName ?? versionId) + ".jar"),
                Sha1: client?["sha1"]?.GetValue<string>() ?? string.Empty,
                Size: client?["size"]?.GetValue<long>() ?? 0,
                Label: "client.jar",
                Kind: Localizer.Tr("客户端"));
        }
        else
        {
            plan.Warnings.Add("版本 JSON 里没有客户端 jar（downloads.client）");
        }

        // 库 + natives
        foreach (var node in vj["libraries"] as JsonArray ?? new JsonArray())
        {
            if (node is not JsonObject lib || !allow(lib["rules"]))
            {
                continue;
            }

            var downloads = lib["downloads"] as JsonObject;
            var artifact = downloads?["artifact"] as JsonObject;
            var artifactUrl = artifact?["url"]?.GetValue<string>();
            var artifactPath = artifact?["path"]?.GetValue<string>();
            var libName = lib["name"]?.GetValue<string>() ?? string.Empty;

            if (!string.IsNullOrEmpty(artifactUrl) && !string.IsNullOrEmpty(artifactPath))
            {
                plan.Libraries.Add(new InstallTask(
                    Url: artifactUrl,
                    Path: Path.Combine(mcDir, "libraries", artifactPath.Replace('/', Path.DirectorySeparatorChar)),
                    Sha1: artifact?["sha1"]?.GetValue<string>() ?? string.Empty,
                    Size: artifact?["size"]?.GetValue<long>() ?? 0,
                    Label: Path.GetFileName(artifactPath),
                    Kind: Localizer.Tr("库")));
            }
            else if (libName.Length > 0 && downloads?["classifiers"] is not JsonObject)
            {
                // ⚠️ 只有 classifiers 的库（如 lwjgl-platform）没有主 artifact，不能按 maven name 拼主 jar，否则必然 404。
                var rel = MavenToPath(libName);
                if (rel.Length > 0)
                {
                    var baseUrl = (lib["url"]?.GetValue<string>() ?? "https://libraries.minecraft.net/")
                        .TrimEnd('/');
                    plan.Libraries.Add(new InstallTask(
                        Url: $"{baseUrl}/{rel}",
                        Path: Path.Combine(mcDir, "libraries", rel.Replace('/', Path.DirectorySeparatorChar)),
                        Label: Path.GetFileName(rel),
                        Kind: Localizer.Tr("库")));
                }
                else
                {
                    plan.Warnings.Add($"库名看不懂，跳过：{libName}");
                }
            }

            // native（老版本才需要：把 dll/so 从 jar 里解出来）
            var key = NativeKeyForThisOs(lib);
            if (key.Length > 0)
            {
                var native = (downloads?["classifiers"] as JsonObject)?[key] as JsonObject;
                var nativeUrl = native?["url"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(nativeUrl))
                {
                    var nativePath = native?["path"]?.GetValue<string>() ?? string.Empty;
                    plan.Natives.Add(new InstallTask(
                        Url: nativeUrl,
                        Path: Path.Combine(mcDir, "libraries", nativePath.Replace('/', Path.DirectorySeparatorChar)),
                        Sha1: native?["sha1"]?.GetValue<string>() ?? string.Empty,
                        Size: native?["size"]?.GetValue<long>() ?? 0,
                        Label: Path.GetFileName(nativePath),
                        Kind: Localizer.Tr("原生库")));
                }
                else
                {
                    plan.Warnings.Add($"这个库说要 native（{key}）但 JSON 里没有：{libName}");
                }
            }
        }

        // 资源索引
        var index = vj["assetIndex"] as JsonObject;
        var indexUrl = index?["url"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(indexUrl))
        {
            var indexId = index?["id"]?.GetValue<string>() ?? "index";
            plan.AssetIndex = new InstallTask(
                Url: indexUrl,
                Path: Path.Combine(mcDir, "assets", "indexes", indexId + ".json"),
                Sha1: index?["sha1"]?.GetValue<string>() ?? string.Empty,
                Size: index?["size"]?.GetValue<long>() ?? 0,
                Label: "assets index",
                Kind: Localizer.Tr("资源索引"));
        }

        // 资源文件（objects）
        if (assetIndex?["objects"] is JsonObject objects)
        {
            foreach (var (name, info) in objects)
            {
                if (info is not JsonObject obj)
                {
                    continue;
                }
                var hash = obj["hash"]?.GetValue<string>() ?? string.Empty;
                if (hash.Length == 0)
                {
                    continue;
                }
                plan.Assets.Add(new InstallTask(
                    Url: $"{ResourcesBase}/{hash[..2]}/{hash}",
                    Path: Path.Combine(mcDir, "assets", "objects", hash[..2], hash),
                    Sha1: hash,                 // 资源文件的 sha1 就是它的文件名
                    Size: obj["size"]?.GetValue<long>() ?? 0,
                    Label: name,
                    Kind: Localizer.Tr("资源")));
            }
        }

        // log4j 配置（1.7~1.17 的日志配置，有就下）
        var loggingCfg = (vj["logging"] as JsonObject)?["client"] as JsonObject;
        var logFile = loggingCfg?["file"] as JsonObject;
        var logId = logFile?["id"]?.GetValue<string>();
        var logUrl = logFile?["url"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(logId) && !string.IsNullOrEmpty(logUrl))
        {
            plan.Logging = new InstallTask(
                Url: logUrl,
                Path: Path.Combine(mcDir, "assets", "log_configs", logId),
                Sha1: logFile?["sha1"]?.GetValue<string>() ?? string.Empty,
                Size: logFile?["size"]?.GetValue<long>() ?? 0,
                Label: "log4j config",
                Kind: Localizer.Tr("日志配置"));
        }

        return plan;
    }
}
