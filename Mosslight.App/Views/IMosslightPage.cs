using System.Windows.Controls;

namespace Mosslight.App.Views;

/// <summary>
/// 页面契约：主窗口切页时用它找到"要依次入场的那个容器"。
///
/// ⚠️ 为什么不让主窗口自己去翻视觉树：页面内部布局是页面自己的事
/// （有的页面是一列卡片、有的是两栏），外面猜不准。页面自己声明最准。
/// 返回 null = 这个页面不做入场动画。
/// </summary>
public interface IMosslightPage
{
    Panel? AnimationHost { get; }
}
