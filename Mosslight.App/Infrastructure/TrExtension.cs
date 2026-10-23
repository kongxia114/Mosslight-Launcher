using System.Windows.Markup;
using Mosslight.Core.Localization;

namespace Mosslight.App.Infrastructure;

/// <summary>
/// XAML 里写中文源文案的标记扩展；⚠️ 切换语言时**已经建好的控件不会自己变**（那边靠
/// TranslatableWidget 重新贴一遍）。
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
