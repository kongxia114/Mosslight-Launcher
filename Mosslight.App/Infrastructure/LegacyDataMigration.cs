using System;
using System.IO;
using Mosslight.Core.Settings;

namespace Mosslight.App.Infrastructure;

/// <summary>
/// 把旧位置（`%APPDATA%\Mosslight`）里的数据搬进新的配置目录（`&lt;启动器目录&gt;\Mosslight`）；⚠
/// ️ 复制失败（权限、磁盘满）只记日志，不拦启动：最坏的情况就是回到默认设置。
/// </summary>
public static class LegacyDataMigration
{
    private static readonly string[] Files = { "config.json", "favorites.json", "accounts.json" };

    public static void ImportFromAppDataOnce(string baseDir)
    {
        try
        {
            var target = Path.Combine(baseDir, AppSettings.DataDirName);
            if (File.Exists(Path.Combine(target, "config.json")))
            {
                return;
            }

            var legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppSettings.DataDirName);
            if (!File.Exists(Path.Combine(legacy, "config.json")))
            {
                return;
            }

            Directory.CreateDirectory(target);
            foreach (var name in Files)
            {
                var source = Path.Combine(legacy, name);
                if (File.Exists(source))
                {
                    File.Copy(source, Path.Combine(target, name), overwrite: false);
                }
            }

            var skins = Path.Combine(legacy, "skins");
            if (Directory.Exists(skins))
            {
                CopyDirectory(skins, Path.Combine(target, "skins"));
            }

            RegistryStore.WriteString("Migration", "FromAppData",
                DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            App.WriteLauncherLog($"已把旧配置从 {legacy} 复制到 {target}（旧文件保留）");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.WriteLauncherLog($"迁移旧配置失败，按默认设置启动：{ex.Message}");
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false);
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
        }
    }
}
