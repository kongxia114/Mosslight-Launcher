namespace Mosslight.Core.Services.Engine;

// 启动器品牌：通过 `--versionType` 让游戏主界面显示启动器名。
// ⚠️ 只能控制 `--versionType`：不要试图用 version.json 或资源包改版本名，Fabric 会绕开 classpath，Forge / NeoForge 则用自己的品牌块顶掉原版那行。

/// <summary>启动器的版本信息（就是 `--versionType`）。</summary>
public static class Branding
{
    /// <summary>版本类型里用来分隔"启动器 / 加载器"的符号（跟 PCL 一样用斜杠）。</summary>
    public const string Separator = "/";

    /// <summary>启动器名的默认值（跟 Core/AppInfo 一致）。</summary>
    public static string AppName => AppInfo.AppName;

    /// <summary>拼「版本信息」：`Mosslight/Fabric （113 个模组）`。</summary>
    public static string LaunchBrand(string? loaderLabel = "", int modCount = 0, string? brand = null)
    {
        var name = (brand ?? AppName).Trim();
        var loader = (loaderLabel ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return string.Empty;
        }

        var text = loader.Length > 0 ? $"{name}{Separator}{loader}" : name;
        if (modCount > 0)
        {
            // 跟着启动器的界面语言走（它显示在游戏里，没有自己的语言）
            text += Localization.Localizer.Tr("（{n} 个模组）", ("n", modCount));
        }
        return text;
    }

    /// <summary>
    /// 数一下 `mods/` 里有多少个 jar（给上面那个 `（N 个模组）` 用）；⚠️ 只看**第一层**的 `*.jar`：
    /// `mods/` 下面还可能有 `1.20.1/` 这种按版本分的子目录、`.disabled` 之类的文件，递归数会把
    /// 它们也算进去（PCL 也只数第一层）。
    /// </summary>
    public static int CountMods(params string?[] dirs)
    {
        var total = 0;
        foreach (var dir in dirs)
        {
            if (string.IsNullOrEmpty(dir))
            {
                continue;
            }
            try
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    if (string.Equals(Path.GetExtension(file), ".jar", StringComparison.OrdinalIgnoreCase))
                    {
                        total++;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 读不了就当 0
            }
        }
        return total;
    }

    /// <summary>
    /// 这个版本该去哪些目录数 mod（版本隔离的版本看自己目录，否则看公共 mods）；⚠️ 隔离与否
    /// **不能只看着目录里有没有 `mods/`**：扫描器的判定准得多 （它认 `saves/`、`options.txt` 这些
    /// 特征），所以调用方知道就传 isolated；不知道就两边都数（宁可多算一个也不显示成 0）。
    /// </summary>
    public static List<string> ModDirs(string? mcDir, string? versionDir, bool? isolated)
    {
        var dirs = new List<string>();
        if (!string.IsNullOrEmpty(versionDir))
        {
            dirs.Add(Path.Combine(versionDir, "mods"));
        }
        if (isolated is not true && !string.IsNullOrEmpty(mcDir))
        {
            dirs.Add(Path.Combine(mcDir, "mods"));
        }
        return dirs;
    }
}
