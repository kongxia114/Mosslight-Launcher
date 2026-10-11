namespace Mosslight.Core;

/// <summary>
/// 应用元信息（对应 1.0.0 的 core/app_info.py）。
///
/// 版本号只在这一处维护 —— 1.0.0 那边的教训：旧代码把 "v0.1.0-dev" 硬编码在
/// 侧边栏里，而实际发布的 tag 早就到 v0.0.5-alpha 了，界面上显示的版本号是假的。
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

    /// <summary>2.0 是重写版，版本号从头开 2.x —— 不要跟 1.0.0 的 v1.0.0 混。</summary>
    public const string AppVersion = "v2.0.0-alpha";

    /// <summary>仓库地址（关于页「查看源代码」用）。</summary>
    public const string RepoUrl = "https://github.com/kongxia114/Mosslight-Launcher";

    /// <summary>数据目录名。1.0.0 改名时**没有**动它（改了老用户配置会变孤儿）。</summary>
    public const string DataDirName = "Mosslight";
}
