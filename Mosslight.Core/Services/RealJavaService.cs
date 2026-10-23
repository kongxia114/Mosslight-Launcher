using Mosslight.Core.Services.Engine;
using Mosslight.Core.Settings;

namespace Mosslight.Core.Services;

// Java 探测的真实现：扫描在 JavaScanner，这里只负责缓存、挑默认 Java、转换结果。

/// <summary>真实的 Java 探测服务。</summary>
public sealed class RealJavaService : IJavaService
{
    private readonly AppSettings _settings;
    private readonly JavaScanner _scanner;
    private readonly RealGameService _games;
    private readonly object _lock = new();

    private List<JavaProbe> _probes = new();
    private List<JavaRuntime> _detected = new();
    private bool _scanned;

    public RealJavaService(AppSettings settings, RealGameService games, string configDir)
    {
        _settings = settings;
        _games = games;
        _scanner = new JavaScanner(configDir);
    }

    public bool IsPrototype => false;

    public IReadOnlyList<JavaRuntime> Detected
    {
        get
        {
            lock (_lock)
            {
                return _detected;
            }
        }
    }

    /// <summary>扫过至少一次没有（界面用它区分"还没扫"和"扫了但一个都没有"）。</summary>
    public bool HasScanned
    {
        get
        {
            lock (_lock)
            {
                return _scanned;
            }
        }
    }

    /// <summary>上一次扫描的说明文字（`正在检测 N 个候选…` 那种），给状态行用。</summary>
    public string LastScanNote { get; private set; } = string.Empty;

    public Task<IReadOnlyList<JavaRuntime>> ScanAsync(CancellationToken token = default)
        => Task.Run<IReadOnlyList<JavaRuntime>>(() =>
        {
            Scan(useCache: true, token);
            return Detected;
        }, token);

    /// <summary>强制重扫（"重新扫描"按钮用，忽略缓存）。</summary>
    public Task<IReadOnlyList<JavaRuntime>> RescanAsync(CancellationToken token = default)
        => Task.Run<IReadOnlyList<JavaRuntime>>(() =>
        {
            Scan(useCache: false, token);
            return Detected;
        }, token);

    private void Scan(bool useCache, CancellationToken token)
    {
        // 游戏目录可能刚改过，让它先自己重扫一遍再拿（这样自带运行时也找得到）
        var mcDir = _games.ResolveMinecraftDir();
        var mcDirs = new List<string> { mcDir };
        mcDirs.AddRange(VersionScanner.ScanMinecraftRoots());

        var probes = _scanner.FindJavas(
            mcDirs.Distinct(StringComparer.OrdinalIgnoreCase),
            onProgress: text => LastScanNote = text,
            useCache: useCache,
            token: token);

        var preferred = _settings.GetString("java_path") ?? string.Empty;

        lock (_lock)
        {
            _probes = probes;
            // 界面上"默认"那个 = 配置里指定的；没指定就取探测到的第一个可用的
            _detected = probes
                .Select(p => p.ToRuntime(IsPreferred(p, preferred)))
                .ToList();
            _scanned = true;
        }
    }

    private static bool IsPreferred(JavaProbe probe, string preferred)
    {
        if (string.IsNullOrEmpty(preferred) || !probe.Usable)
        {
            return false;
        }
        try
        {
            return string.Equals(
                Path.GetFullPath(probe.Path),
                Path.GetFullPath(preferred),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(probe.Path, preferred, StringComparison.OrdinalIgnoreCase);
        }
    }

    // 挑 Java

    /// <summary>这个版本**至少**要哪个 Java 主版本。</summary>
    public int RequiredMajor(GameVersion version)
    {
        if (version.JavaMajor is > 0)
        {
            return version.JavaMajor.Value;
        }

        var vj = LoadVersionJson(version);
        return JavaScanner.RequiredMajor(vj);
    }

    /// <summary>读这个版本的（已合并的）JSON。</summary>
    private VersionJson? LoadVersionJson(GameVersion version)
    {
        if (!string.IsNullOrEmpty(version.VersionJsonPath) && File.Exists(version.VersionJsonPath))
        {
            try
            {
                var raw = JsonIO.ParseBytes(File.ReadAllBytes(version.VersionJsonPath))
                    as System.Text.Json.Nodes.JsonObject;
                var jsonId = raw?["id"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(jsonId))
                {
                    // ⚠️ VersionJsonLoader 要的是游戏目录，必须从 `.minecraft\versions\<id>\<id>.json` 往上退三层；少退一层会找不到 JSON，导致 Java 版本回退到 8。
                    var versionDir = Path.GetDirectoryName(Path.GetFullPath(version.VersionJsonPath));
                    var versionsDir = versionDir is null ? null : Path.GetDirectoryName(versionDir);
                    var mcDir = versionsDir is null ? null : Path.GetDirectoryName(versionsDir);
                    if (!string.IsNullOrEmpty(mcDir))
                    {
                        return VersionJsonLoader.TryLoad(mcDir!, jsonId);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException
                                          or UnauthorizedAccessException or InvalidOperationException)
            {
                // 落到下面的兜底路径
            }
        }

        return VersionJsonLoader.TryLoad(_games.ResolveMinecraftDir(), version.Id);
    }

    /// <summary>给这个版本挑一个合适的 Java（挑不出来就 null，界面要能显示"未找到"）。</summary>
    public JavaRuntime? PickFor(GameVersion version)
    {
        var required = RequiredMajor(version);
        var preferred = _settings.GetString("java_path") ?? string.Empty;

        List<JavaProbe> probes;
        lock (_lock)
        {
            probes = _probes;
        }

        var (java, note) = JavaScanner.PickJava(probes, required, preferred);
        LastPickNote = note;
        return java?.ToRuntime(IsPreferred(java, preferred));
    }

    /// <summary>
    /// 上一次挑 Java 的说明（`没有 Java 21，用 Java 17 顶替（可能起不来）` 这种）。
    /// </summary>
    public string LastPickNote { get; private set; } = string.Empty;
}
