using System.IO;
using Microsoft.Win32;

namespace Mosslight.App.Infrastructure;

/// <summary>系统主题：Windows 现在是不是深色？⚠️ 读不到时返回深色。</summary>
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
