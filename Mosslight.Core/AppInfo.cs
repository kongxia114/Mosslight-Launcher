using System.Reflection;

namespace Mosslight.Core;

/// <summary>设计说明见 docs/design-notes.md#mosslight-core-appinfo-cs</summary>
public static class AppInfo
{
    /// <summary>界面上的名字（侧边栏、关于页）。</summary>
    public const string AppName = "Mosslight";

    /// <summary>给 Windows 看的完整名字（窗口标题、任务栏）。</summary>
    public const string AppDisplayName = "Mosslight Launcher";

    /// <summary>
    /// 版本号（带 `v` 前缀，比如 `v2.0.0-alpha`），**从程序集属性读出来的**。
    ///
    /// ⚠️ 读不到（理论上不可能）就退回 <see cref="FallbackVersion"/> ——
    ///    界面宁可显示一个写死的版本号，也不能显示空白或者抛异常。
    /// </summary>
    public static string AppVersion { get; } = ReadVersion();

    /// <summary>兜底用的版本号（只在读不到程序集属性时用）。</summary>
    private const string FallbackVersion = "v2.0.0-alpha";

    /// <summary>
    /// 从 `AssemblyInformationalVersion` 取版本号，整理成界面上要的样子。
    ///
    /// ⚠️ 要**去掉 `+` 后面那段**：MSBuild 默认会拼上 git 短 SHA
    ///    （`2.0.0-alpha+78a66b3`），那是给排错用的、不是给用户看的
    ///    （用户截图里「产品版本」后面那一串就是它）。
    ///    `Directory.Build.props` 里已经关了那个开关，这里再兜一层：
    ///    别人手动 `-p:IncludeSourceRevisionInInformationalVersion=true` 构建时也漏不出去。
    /// ⚠️ 空串也要能处理（属性缺失 / 以后换了构建方式）。
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

        // 界面上一直带 `v`（1.0.0 就是 `v1.0.0`），这里补上
        return raw.StartsWith('v') || raw.StartsWith('V') ? raw : "v" + raw;
    }

    /// <summary>仓库地址（关于页「查看源代码」用）。</summary>
    public const string RepoUrl = "https://github.com/kongxia114/Mosslight-Launcher-2";

    /// <summary>数据目录名。1.0.0 改名时**没有**动它（改了老用户配置会变孤儿）。</summary>
    public const string DataDirName = "Mosslight";
}
