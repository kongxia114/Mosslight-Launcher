namespace Mosslight.Core.Appearance;

/// <summary>设计说明见 docs/design-notes.md#mosslight-core-appearance-backgroundmodes-cs</summary>
public static class BackgroundModes
{
    public const string Fill = "fill";
    public const string Fit = "fit";
    public const string Stretch = "stretch";
    public const string Center = "center";
    public const string Span = "span";

    public const string Default = Center;
    public const int DefaultDim = 90;

    /// <summary>铺法 → 界面文案。</summary>
    public static readonly IReadOnlyDictionary<string, string> Labels =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Fill] = "填充",       // 等比放大到铺满，多出来的裁掉（不留黑边）
            [Fit] = "适应",        // 等比缩放到全图可见，不够的地方留黑边
            [Stretch] = "拉伸",    // 不管比例直接拉满（会变形）
            [Center] = "居中",     // 原始大小居中放一张
            [Span] = "跨区",       // 铺满宽度、纵向居中裁切（多屏时"横跨所有显示器"）
        };

    /// <summary>下拉框里的顺序。</summary>
    public static readonly IReadOnlyList<string> Order = new[] { Fill, Fit, Stretch, Center, Span };

    /// <summary>每种铺法的一句话说明（设置界面里当提示用）。</summary>
    public static readonly IReadOnlyDictionary<string, string> Hints =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Fill] = "等比放大铺满窗口，多出来的部分裁掉 —— 最常用",
            [Fit] = "等比缩放到整张图都能看见，不够的地方留边",
            [Stretch] = "直接拉满窗口，比例会变形",
            [Center] = "原图大小放中间，四周留边",
            [Span] = "横向铺满、纵向居中裁切（对应多显示器横跨显示）",
        };

    /// <summary>是不是认识的铺法。</summary>
    public static bool Contains(string? mode) => mode is not null && Labels.ContainsKey(mode);

    /// <summary>
    /// "没设背景"时预览色块用的颜色（深色主题的页面色）。
    /// ⚠️ 真正的背景画布**不用**它 —— 画布问当前主题要 `bg_page`，
    /// 不然浅色主题下会画成深灰。
    /// </summary>
    public const string FallbackColor = "#1b1c1f";

    /// <summary>卡片透明度范围。下限给 10 而不是 0：全透明卡片就看不见了，等于界面坏了。</summary>
    public const int MinCardOpacity = 10;

    public const int MaxCardOpacity = 100;
}
