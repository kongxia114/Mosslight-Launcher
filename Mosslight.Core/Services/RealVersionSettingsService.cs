using Mosslight.Core.Services.Engine;
using Mosslight.Core.Settings;

namespace Mosslight.Core.Services;

// ============================================================================
//  按版本覆盖启动参数的**服务包装**
//
//  真正的逻辑全在 <see cref="Engine.VersionSettings"/> 里（三态、类型纠正、
//  原子写、min/max 互夹，都逐值验过）。这一层只做两件事：
//
//    1. 把 `LauncherDefaults()` 从"启动器默认值从哪来"收成**一处** ——
//       以前这段是抄在首页的 `_effective()` 里的，版本设置页要用就得再抄一份，
//       两份迟早不一致（1.0.0 就是这么踩的）。
//    2. 给界面一个接口，别让它直接 new 引擎类。
// ============================================================================

/// <summary>真实的按版本覆盖参数服务。</summary>
public sealed class RealVersionSettingsService : IVersionSettingsService
{
    private readonly AppSettings _settings;
    private readonly VersionSettings _engine;

    public RealVersionSettingsService(AppSettings settings, VersionSettings engine)
    {
        _settings = settings;
        _engine = engine;
    }

    public bool IsPrototype => false;

    public PerVersionSettings Get(string versionId) => _engine.Get(versionId);

    public bool Update(string versionId, PerVersionSettings overrides)
        => _engine.Update(versionId, overrides);

    public bool Forget(string versionId) => _engine.Forget(versionId);

    public IReadOnlyList<string> CustomizedVersions() => _engine.CustomizedVersions();

    public EffectiveSettings Resolve(string versionId, EffectiveSettings defaults)
        => _engine.Resolve(versionId, defaults);

    /// <summary>
    /// 启动器这一层的默认值（界面上「跟随全局设置」跟的就是它）。
    ///
    /// 放在这里是为了**只有一个地方**知道"默认值从哪来"。
    /// 跟 <see cref="RealLaunchService"/> 用的是同一套 —— 那边也调这个的话，
    /// 两边就不会出现"界面显示 4096、实际按 2048 启动"这种事。
    /// </summary>
    public EffectiveSettings LauncherDefaults() => new(
        JavaPath: _settings.GetString("java_path") ?? string.Empty,
        MinMemory: _settings.MinMemory,
        MaxMemory: _settings.MaxMemory,
        // 启动器没有"全局 JVM 参数"这一项，所以默认就是空
        ExtraJvmArgs: string.Empty,
        ExtraGameArgs: _settings.GetString("game_args") ?? string.Empty,
        CustomInfo: string.Empty,
        ServerAddress: string.Empty,
        Icon: string.Empty);
}

/// <summary>按版本覆盖参数的假实现（自检的"全是假数据"那条路用）。</summary>
public sealed class StubVersionSettingsService : IVersionSettingsService
{
    private readonly Dictionary<string, PerVersionSettings> _data = new(StringComparer.Ordinal);
    private readonly AppSettings? _settings;

    public StubVersionSettingsService(AppSettings? settings = null)
    {
        _settings = settings;
    }

    public bool IsPrototype => true;

    public PerVersionSettings Get(string versionId)
        => _data.TryGetValue(versionId, out var s) ? s : new PerVersionSettings();

    public bool Update(string versionId, PerVersionSettings overrides)
    {
        if (overrides.IsEmpty)
        {
            _data.Remove(versionId);
        }
        else
        {
            _data[versionId] = overrides;
        }
        return true;
    }

    public bool Forget(string versionId) => Update(versionId, new PerVersionSettings());

    public IReadOnlyList<string> CustomizedVersions() => _data.Keys.ToList();

    public EffectiveSettings Resolve(string versionId, EffectiveSettings defaults)
        => defaults;

    public EffectiveSettings LauncherDefaults() => new(
        MinMemory: _settings?.MinMemory ?? 512,
        MaxMemory: _settings?.MaxMemory ?? 2048);
}
