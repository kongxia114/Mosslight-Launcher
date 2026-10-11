using System.Windows.Markup;
using Mosslight.Core.Localization;

namespace Mosslight.App.Infrastructure;

/// <summary>
/// XAML 里写中文源文案的标记扩展：
///
///     xmlns:loc="clr-namespace:Mosslight.App.Infrastructure"
///     &lt;TextBlock Text="{loc:Tr 启动游戏}" /&gt;
///
/// 为什么不直接把中文写在 XAML 里：那样就绕过了 i18n（1.0.0 里所有文案都过
/// `tr()`，改成别的语言时是整份词典换掉）。这个扩展让 XAML 也能走同一套词典 ——
/// 漏翻 / 打错 key 只会退回中文显示，永远不会把 key 糊到用户脸上。
///
/// ⚠️ 切换语言时**已经建好的控件不会自己变**（跟 1.0.0 一样：
/// 那边靠 TranslatableWidget 重新贴一遍）。原型阶段的做法是切语言后重建页面。
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string source) => Source = source;

    /// <summary>中文源文案（也是词典的 key）。</summary>
    public string Source { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Localizer.Tr(Source);
}
