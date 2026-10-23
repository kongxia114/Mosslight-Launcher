using System.Windows.Controls;

namespace Mosslight.App.Views;

/// <summary>
/// 页面契约：主窗口切页时用它找到"要依次入场的那个容器"；⚠️ 为什么不让主窗口自己去翻视觉树：页面
/// 内部布局是页面自己的事 （有的页面是一列卡片、有的是两栏），外面猜不准。
/// </summary>
public interface IMosslightPage
{
    Panel? AnimationHost { get; }
}
