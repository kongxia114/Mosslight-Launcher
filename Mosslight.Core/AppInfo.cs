using System.Reflection;

namespace Mosslight.Core;

/// <summary>
/// 应用元信息（对应 1.0.0 的 core/app_info.py）。
///
/// 版本号**只有一处来源**：仓库根 `Directory.Build.props` 里的 `&lt;Version&gt;`。
/// 构建时它会变成程序集的 `AssemblyInformationalVersion`（= exe 属性里的「产品版本」），
/// 这里再把它读出来给界面用 —— 于是"界面上写的"和"exe 属性里写的"**不可能对不上**。
///
/// ## 为什么不再用 const（2026-10 改的）
/// 1.0.0 的教训是反过来的：旧代码把 "v0.1.0-dev" 硬编码在侧边栏里，而实际发布的
/// tag 早就到 v0.0.5-alpha 了，界面上显示的是假的。2.0 一开始改成"只在
/// `AppInfo.AppVersion` 这一处维护"，但发版时**要改源码**才能把它换成 tag 的版本
/// （release.yml 里去正则替换 + 读回来断言，正则写歪了就发版失败）。
/// 现在版本号是构建参数：`dotnet build -p:Version=2.1.0-beta` 一传，
/// exe 属性和界面**一起**变，源码一个字都不用动。
///
/// ## 两个名字是**故意分开**的
///   AppName        界面上自己叫的名字：侧边栏左上角、关于页。短、干净
///   AppDisplayName Windows 那边看到的：窗口标题、任务栏、exe 属性
/// </summary>
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
    public const string RepoUrl = "https://github.com/kongxia114/Mosslight-Launcher";

    /// <summary>数据目录名。1.0.0 改名时**没有**动它（改了老用户配置会变孤儿）。</summary>
    public const string DataDirName = "Mosslight";
}
