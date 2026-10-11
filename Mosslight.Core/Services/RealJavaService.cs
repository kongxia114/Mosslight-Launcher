using Mosslight.Core.Services.Engine;
using Mosslight.Core.Settings;

namespace Mosslight.Core.Services;

// ============================================================================
//  Java 探测的真实现（移植自 1.0.0 的 core/java.py）
//
//  跟 <see cref="RealGameService"/> 一个路子：扫描全在 <see cref="JavaScanner"/>，
//  这里只管缓存、按设置挑默认 Java、把结果转成界面认的 <see cref="JavaRuntime"/>。
// ============================================================================

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

    /// <summary>
    /// 重新扫一遍。
    ///
    /// ⚠️ **必须放到线程池里跑**：探测要起 java 进程（实测 12 个候选 5 秒多）。
    ///    在 UI 线程上直接 await 一个同步实现 = 界面卡死 5 秒。
    ///    这里用 Task.Run 包住，而且默认**用缓存**（第二次启动是毫秒级）。
    /// </summary>
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

    // ---------- 挑 Java ----------

    /// <summary>
    /// 这个版本**至少**要哪个 Java 主版本。
    ///
    /// 扫描时已经读过 javaVersion 了（<see cref="GameVersion.JavaMajor"/>），直接用；
    /// 读不到再回退去读一遍 JSON —— 老版本（1.12 及以前）没有 `javaVersion` 字段，
    /// 那时候按 8 算。
    /// </summary>
    public int RequiredMajor(GameVersion version)
    {
        if (version.JavaMajor is > 0)
        {
            return version.JavaMajor.Value;
        }

        var vj = LoadVersionJson(version);
        return JavaScanner.RequiredMajor(vj);
    }

    /// <summary>
    /// 读这个版本的（已合并的）JSON。
    ///
    /// 路径有三个来源，按可靠性排：
    ///   1. 扫描器记下的 <see cref="GameVersion.VersionJsonPath"/> —— 最准，
    ///      它知道"文件夹名和 json 里的 id 不一样"这种情况
    ///   2. `&lt;游戏目录&gt;/versions/&lt;id&gt;/&lt;id&gt;.json`
    ///   3. 放弃（返回 null，调用方按默认值走）
    /// </summary>
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
                    return VersionJsonLoader.TryLoad(
                        Path.GetDirectoryName(Path.GetDirectoryName(version.VersionJsonPath)!)!, jsonId);
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

    /// <summary>上一次挑 Java 的说明（`没有 Java 21，用 Java 17 顶替（可能起不来）` 这种）。界面可以显示它。</summary>
    public string LastPickNote { get; private set; } = string.Empty;
}
