using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;
using Mosslight.Core.Theme;

namespace Mosslight.App.Infrastructure;

/// <summary>
/// 把主窗口那套"自绘边框"套到别的窗口上（对话框）；⚠️ `CaptionHeight = 0`：对话框
/// **不留标题栏区域**，所以也没有拖动条 —— 它们都是居中的模态窗，关闭走底部按钮或 Esc。
/// </summary>
public static class FramelessWindow
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>把这个窗口改成无系统边框 + 圆角。</summary>
    public static void Apply(Window window)
    {
        var chrome = new WindowChrome
        {
            CaptionHeight = 0,
            // 能调整大小的窗口要靠这圈"透明边框"来拉伸；固定大小的不要（会多出一条感应区）
            ResizeBorderThickness = new Thickness(window.ResizeMode == ResizeMode.CanResize ? 6 : 0),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        };
        WindowChrome.SetWindowChrome(window, chrome);
        window.WindowStyle = WindowStyle.None;

        window.SourceInitialized += (_, _) => ApplyDwm(window);

        // ⚠️ **双击边框/标题区不该最大化**。
        // WindowChrome 的默认行为：在非客户区双击 → 系统发 WM_NCLBUTTONDBLCLK → 窗口最大化。
        // 这里把那条消息吃掉，系统收不到就不会自己变大；手动拖边框拉伸不受影响。
        window.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(window) is HwndSource source)
            {
                source.AddHook(SwallowNonClientDoubleClick);
            }
        };
    }

    // WM_NCLBUTTONDBLCLK：在标题栏 / 边框上双击。
    private const int WmNcLButtonDblClk = 0x00A3;

    private static IntPtr SwallowNonClientDoubleClick(
        IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmNcLButtonDblClk)
        {
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>圆角 + 深色边框（DWM）。</summary>
    public static void ApplyDwm(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            // 2 = DWMWCP_ROUND（Win11 默认圆角）
            var preferRound = DwmwcpRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preferRound, sizeof(int));

            // 边框色跟随当前生效的主题（不是重新读配置：切主题后这里要跟着变）
            var dark = ThemeApplier.CurrentMode == PaletteMode.Dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // 老系统上没有这两个属性：保持直角
        }
    }
}
