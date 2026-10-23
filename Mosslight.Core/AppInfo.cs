using System.Reflection;

namespace Mosslight.Core;

public static class AppInfo
{
    /// <summary>界面上的名字（侧边栏、关于页）。</summary>
    public const string AppName = "Mosslight";

    /// <summary>给 Windows 看的完整名字（窗口标题、任务栏）。</summary>
    public const string AppDisplayName = "Mosslight Launcher";

    /// <summary>
    /// 版本号（带 `v` 前缀，比如 `v2.0.0-alpha`），**从程序集属性读出来的**；⚠️ 读不到（理论上不
    /// 可能）就退回 FallbackVersion —— 界面宁可显示一个写死的版本号，也不能显示空白或者抛异常。
    /// </summary>
    public static string AppVersion { get; } = ReadVersion();

    // 兜底用的版本号（只在读不到程序集属性时用）。
    private const string FallbackVersion = "v2.0.0-alpha";

    /// <summary>
    /// 从 `AssemblyInformationalVersion` 取版本号，整理成界面上要的样子；⚠️ 要
    /// **去掉 `+` 后面那段**：MSBuild 默认会拼上 git 短 SHA （`2.0.0-alpha+78a66b3`），那是给排错
    /// 用的、不是给用户看的 （用户截图里「产品版本」后面那一串就是它）。
    /// </summary>
    private static string ReadVersion()
    {
        var raw = typeof(AppInfo).Assembly
                      .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                      .InformationalVersion
                  ?? string.Empty;

        var plus = raw.IndexOf('+');
        if (plus >= 0)
        {
            raw = raw[..plus];
        }
        raw = raw.Trim();

        if (raw.Length == 0)
        {
            return FallbackVersion;
        }

        return raw.StartsWith('v') || raw.StartsWith('V') ? raw : "v" + raw;
    }

    /// <summary>仓库地址（关于页「查看源代码」用）。</summary>
    public const string RepoUrl = "https://github.com/kongxia114/Mosslight-Launcher";

    public const string DataDirName = "Mosslight";
}
