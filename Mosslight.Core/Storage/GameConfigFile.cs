using System.Text;

namespace Mosslight.Core.Storage;

/// <summary>
/// 游戏目录里的 `config.ini`；⚠️ 文件不存在就写一份默认的（否则用户打开一看什么都没有）；写不了要
/// 让调用方弹窗并退出（`Ensure` 返回 false + 原因），不能装作没事继续跑。
/// </summary>
public static class GameConfigFile
{
    public const string FileName = "config.ini";

    public const string DefaultContent =
        "; Mosslight 的游戏目录配置（只认这个文件，不读 %APPDATA%）\r\n" +
        "; minecraft_dir = 游戏目录；留空表示用启动器所在目录\r\n" +
        "[game]\r\n" +
        "minecraft_dir =\r\n";

    public static string PathFor(string gameDir) => Path.Combine(gameDir, FileName);

    /// <summary>确保 `&lt;gameDir&gt;\config.ini` 存在（不存在就按默认内容建一份）。</summary>
    public static bool Ensure(string gameDir, out string path, out string? error)
    {
        path = PathFor(gameDir);
        error = null;
        try
        {
            Directory.CreateDirectory(gameDir);
            if (!File.Exists(path))
            {
                File.WriteAllText(path, DefaultContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or NotSupportedException or System.Security.SecurityException
                                      or ArgumentException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>从 ini 里取 `minecraft_dir`。</summary>
    public static string ReadMinecraftDir(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return string.Empty;
            }
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#') || line.StartsWith('['))
                {
                    continue;
                }
                var eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }
                if (!line[..eq].Trim().Equals("minecraft_dir", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                return line[(eq + 1)..].Trim().Trim('"');
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            // 读不了就当没配
        }
        return string.Empty;
    }
}
