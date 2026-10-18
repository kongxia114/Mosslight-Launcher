namespace Mosslight.Core.Services.Engine;

//  ⚠️ 所以**启动器唯一能控制的就是 `--versionType`**（版本 JSON 里的 `${version_type}`）。
// 设计说明见 docs/design-notes.md#mosslight-core-services-engine-branding-cs

/// <summary>启动器的版本信息（就是 `--versionType`）。</summary>
public static class Branding
{
    /// <summary>版本类型里用来分隔"启动器 / 加载器"的符号（跟 PCL 一样用斜杠）。</summary>
    public const string Separator = "/";

    /// <summary>启动器名的默认值（跟 Core/AppInfo 一致）。</summary>
    public static string AppName => AppInfo.AppName;

    /// <summary>
    /// 拼「版本信息」：`Mosslight/Fabric （113 个模组）`。
    ///
    ///   - 原版：`Mosslight`
    ///   - 带加载器：`Mosslight/Fabric`
    ///   - 装了 mod：尾巴补一句 `（113 个模组）`
    ///
    /// ⚠️ 返回值**不能是 `release`**：游戏那边只有"不是 release"才往主界面那行后面接
    /// （`TitleScreen` 里就是 `if (!"release".equalsIgnoreCase(type)) s += "/" + type`），
    /// 正好用它当"这次不显示品牌"的开关 —— 用户想关掉就把自定义信息填成 `release`。
    /// </summary>
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
    /// 数一下 `mods/` 里有多少个 jar（给上面那个 `（N 个模组）` 用）。
    ///
    /// ⚠️ 只看**第一层**的 `*.jar`：`mods/` 下面还可能有 `1.20.1/` 这种按版本分的子目录、
    ///    `.disabled` 之类的文件，递归数会把它们也算进去（PCL 也只数第一层）。
    /// ⚠️ 目录读不了就当 0 —— 这只是个显示用的数字，不能因为它让启动失败。
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
    /// 这个版本该去哪些目录数 mod（版本隔离的版本看自己目录，否则看公共 mods）。
    ///
    /// ⚠️ 隔离与否**不能只看着目录里有没有 `mods/`**：扫描器的判定准得多
    /// （它认 `saves/`、`options.txt` 这些特征），所以调用方知道就传 isolated；
    /// 不知道就两边都数（宁可多算一个也不显示成 0）。
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
