using Mosslight.Core.Settings;

namespace Mosslight.Core.Services.Engine;

// ============================================================================
//  服务的装配点
//
//  只在这里 new 实现 —— 界面拿到的永远是 <see cref="LauncherServices"/> 和那几个接口。
//  好处是"哪天真要换实现"（比如给测试塞假的），改这一个文件就行，
//  不用去界面里到处找 new。
//
//  ⚠️ 配置目录的解析**只能有一处**（<see cref="AppSettings.ResolveConfigDir"/>）：
//    1.0.0 那边 core/accounts.py 自己抄了一份同名函数、两边都硬编码 "MCLuncher"，
//    做便携模式时立刻就咬人了。这里直接转给它。
// ============================================================================

/// <summary>把真引擎装起来。</summary>
public static class ServiceFactory
{
    /// <summary>
    /// 建一整套真服务。
    ///
    /// <paramref name="settings"/> 是**活的对象**（不是拷贝）：游戏目录、Java 路径
    /// 这些设置改了以后，服务下次自己重读，不用重建整个集合。
    /// </summary>
    public static LauncherServices Create(AppSettings settings, string baseDir)
    {
        var configDir = AppSettings.ResolveConfigDir(baseDir);

        var games = new RealGameService(settings);
        var java = new RealJavaService(settings, games, configDir);
        var accounts = new RealAccountService(configDir);
        // 按版本覆盖参数（versions.json）—— 启动时要合上它，版本设置页也读写它
        var versionSettingsStore = new VersionSettings(configDir);
        var launch = new RealLaunchService(settings, games, java, versionSettingsStore);
        var downloads = new RealDownloadService(settings, configDir, games);
        var loaders = new RealLoaderService(settings, games, downloads, configDir, baseDir);
        var mods = new RealModService(settings);
        var modpacks = new RealModpackService(settings, games, loaders, downloads);
        // 同一个 store 交两份出去：启动那条路直接用它合参数，界面那条路走接口
        var versionSettings = new RealVersionSettingsService(settings, versionSettingsStore);

        return new LauncherServices(games, java, accounts, launch, downloads,
            loaders, mods, modpacks, versionSettings);
    }
}
