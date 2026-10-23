using Mosslight.Core.Settings;

namespace Mosslight.Core.Services.Engine;

// 服务的装配点：只在这里 new 实现，界面拿到的始终是 LauncherServices 和接口。
// ⚠️ 配置目录解析只能走 AppSettings.ResolveConfigDir，别在引擎里另拼一份。

/// <summary>把真引擎装起来。</summary>
public static class ServiceFactory
{
    /// <summary>建一整套真服务。</summary>
    public static LauncherServices Create(AppSettings settings, string baseDir)
    {
        var configDir = AppSettings.ResolveConfigDir(baseDir);

        var games = new RealGameService(settings);
        var java = new RealJavaService(settings, games, configDir);
        var accounts = new RealAccountService(configDir);
        // 按版本覆盖参数（versions.json）—— 启动时要合上它，版本设置页也读写它
        var versionSettingsStore = new VersionSettings(configDir);
        var launch = new RealLaunchService(settings, games, java, versionSettingsStore, accounts);
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
