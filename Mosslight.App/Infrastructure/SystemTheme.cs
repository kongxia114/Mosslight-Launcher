using System.IO;
using Microsoft.Win32;

namespace Mosslight.App.Infrastructure;

/// <summary>
/// 系统主题：Windows 现在是不是深色？
///
/// 读的是 `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize`
/// 的 `AppsUseLightTheme`（0 = 深色，1 = 浅色）—— 就是"设置 → 个性化 → 颜色 →
/// 选择默认应用模式"那一项。
///
/// ⚠️ 读不到时返回**深色**，跟 1.0.0 的 `ui/theme_state.py` 一致
/// （那边 Qt 拿不到 colorScheme 时也是 `return True`）。
/// 启动器自己不发光，猜错了只是观感问题；猜浅色而系统是深色会更刺眼。
/// </summary>
public static class SystemTheme
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            if (key?.GetValue("AppsUseLightTheme") is int value)
            {
                return value == 0;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // 读注册表被拒（组策略/沙箱）就按深色走，不该因此起不来
        }
        return true;
    }
}
