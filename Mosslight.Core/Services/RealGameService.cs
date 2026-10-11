using Mosslight.Core.Services.Engine;
using Mosslight.Core.Settings;

namespace Mosslight.Core.Services;

// ============================================================================
//  版本列表的真实现（移植自 1.0.0 的 core/versions.py 那套）
//
//  这个类很薄是**故意的**：扫描逻辑全在 <see cref="VersionScanner"/> 里，
//  这里只负责「缓存一份结果、按设置解析游戏目录、把错误转给界面」。
//  好处是扫描器可以脱离服务单测（自检里就是这么用的）。
// ============================================================================

/// <summary>真实的版本列表服务。</summary>
public sealed class RealGameService : IGameService
{
    private readonly AppSettings _settings;
    private readonly object _lock = new();

    private List<GameVersion> _installed = new();
    private List<string> _scanErrors = new();
    private string _minecraftDir = string.Empty;

    public RealGameService(AppSettings settings)
    {
        _settings = settings;
        Rescan();
    }

    public bool IsPrototype => false;

    public IReadOnlyList<GameVersion> Installed
    {
        get
        {
            lock (_lock)
            {
                return _installed;
            }
        }
    }

    public IReadOnlyList<string> ScanErrors
    {
        get
        {
            lock (_lock)
            {
                return _scanErrors;
            }
        }
    }

    public string MinecraftDir
    {
        get
        {
            lock (_lock)
            {
                return _minecraftDir;
            }
        }
    }

    public Task<IReadOnlyList<GameVersion>> RefreshAsync(CancellationToken token = default)
        => Task.Run(() =>
        {
            Rescan();
            return Installed;
        }, token);

    /// <summary>同步重扫一遍（构造时和刷新时都走这里）。</summary>
    private void Rescan()
    {
        var scanner = new VersionScanner(ResolveMinecraftDir());
        var scanned = scanner.Scan();

        lock (_lock)
        {
            _installed = scanned.Select(s => s.Version).ToList();
            _scanErrors = scanner.Errors.ToList();
            _minecraftDir = scanner.MinecraftDir;
        }
    }

    /// <summary>
    /// 这次要扫哪个 .minecraft 目录。
    ///
    /// ⚠️ **每次都重新读配置**，不是构造时读一次 —— 用户在设置页改完游戏目录，
    ///    接着调 RefreshAsync 就该扫新目录。把值缓存在字段里的话，
    ///    改了设置要重启启动器才生效（1.0.0 是每次 reload 都重读，照做）。
    /// </summary>
    public string ResolveMinecraftDir()
        => VersionScanner.DefaultMinecraftDir(_settings.MinecraftDir);
}
