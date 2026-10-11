using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Mosslight.App.Views;

/// <summary>
/// 数字框（带上下箭头）。对应 1.0.0 的 <c>QSpinBox</c> —— 设置页的「最小内存」用它。
///
/// ## 行为照 1.0.0 的 QSpinBox
///   · 能直接敲数字；敲非数字字符不进去（<see cref="OnPreviewTextInput"/>）
///   · 上下箭头按 <see cref="Step"/> 步进（1.0.0 是 <c>setSingleStep(512)</c>）
///   · 数字后面跟 <c>" MB"</c>（1.0.0 是 <c>setSuffix(" MB")</c>）
///   · 越界夹到 <see cref="Minimum"/> / <see cref="Maximum"/>
///
/// ⚠️ 失焦才提交（1.0.0 的 QSpinBox 是敲完就发 valueChanged）。
///    刻意选失焦提交：每敲一个数字就写一次 config.json 的话，
///    「3072」会被拆成 3 / 30 / 307 / 3072 四次写盘 —— 1.0.0 的注释里专门抱怨过
///    这种"一次拖动写出几十份 config"的问题（它用 400ms 防抖解决，
///    这里用"敲完再提交"达到同样的效果，也不用引入定时器）。
/// </summary>
public partial class NumberBox : UserControl
{
    /// <summary>值变了（用户改的，程序设值不会触发）。</summary>
    public event EventHandler<int>? ValueChanged;

    /// <summary>正在按程序设值 —— 用来挡住"设值 → 回调 → 又设值"的回环。</summary>
    private bool _updating;

    /// <summary>正在编辑（有焦点）。这时候不要用 Value 覆盖用户敲了一半的文本。</summary>
    private bool _editing;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(NumberBox),
        new FrameworkPropertyMetadata(512, OnValueChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(int), typeof(NumberBox),
        new FrameworkPropertyMetadata(512, OnRangeChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(int), typeof(NumberBox),
        new FrameworkPropertyMetadata(65536, OnRangeChanged));

    /// <summary>上下箭头一次走多少（1.0.0 的 <c>setSingleStep</c>）。</summary>
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(int), typeof(NumberBox),
        new FrameworkPropertyMetadata(512));

    /// <summary>数字后面跟的单位（1.0.0 的 <c>setSuffix(" MB")</c>）。</summary>
    public static readonly DependencyProperty SuffixProperty = DependencyProperty.Register(
        nameof(Suffix), typeof(string), typeof(NumberBox),
        new FrameworkPropertyMetadata(" MB", OnSuffixChanged));

    public int Value
    {
        get => (int)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public int Minimum
    {
        get => (int)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public int Maximum
    {
        get => (int)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public int Step
    {
        get => (int)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public string Suffix
    {
        get => (string)GetValue(SuffixProperty);
        set => SetValue(SuffixProperty, value);
    }

    public NumberBox()
    {
        InitializeComponent();
        RefreshText();
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((NumberBox)d).RefreshText();

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((NumberBox)d).RefreshText();

    private static void OnSuffixChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((NumberBox)d).RefreshText();

    private void RefreshText()
    {
        if (_editing)
        {
            return;
        }
        // 直接用 Editor 而不是 x:Name 生成的字段：构造函数里 InitializeComponent
        // 之后它一定存在；而 DP 回调可能在 InitializeComponent *之前*就来（XAML 赋值顺序）
        if (Editor is null)
        {
            return;
        }
        Editor.Text = Value.ToString(CultureInfo.InvariantCulture) + Suffix;
    }

    // ---------- 交互 ----------

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // 只放数字进去（后缀" MB"是程序贴的，用户不用管）
        foreach (var ch in e.Text)
        {
            if (!char.IsDigit(ch))
            {
                e.Handled = true;
                return;
            }
        }
    }

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Commit();
                e.Handled = true;
                break;
            case Key.Escape:
                // 反悔：把文本恢复成当前值
                _editing = false;
                RefreshText();
                e.Handled = true;
                break;
            case Key.Up:
                StepBy(1);
                e.Handled = true;
                break;
            case Key.Down:
                StepBy(-1);
                e.Handled = true;
                break;
        }
    }

    private void OnEditorLostFocus(object sender, RoutedEventArgs e)
    {
        _editing = false;
        Commit();
    }

    private void OnUpClick(object sender, RoutedEventArgs e) => StepBy(1);

    private void OnDownClick(object sender, RoutedEventArgs e) => StepBy(-1);

    /// <summary>焦点进到输入框（XAML 里没法直接挂 GotFocus 到 TextBox 的内部状态，用重写）。</summary>
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (e.NewFocus == Editor)
        {
            _editing = true;
            // 只选中数字，不含后缀 —— 直接敲就能覆盖掉旧值
            Editor.Select(0, Value.ToString(CultureInfo.InvariantCulture).Length);
        }
    }

    private void StepBy(int steps)
    {
        var next = Coerce(Value + steps * Math.Max(1, Step));
        if (next != Value)
        {
            SetCurrentValue(ValueProperty, next);
        }
        else
        {
            // 顶到边界时也要把文本纠正回来（用户可能敲了 999999 又被夹住）
            RefreshText();
        }
        ValueChanged?.Invoke(this, next);
    }

    private void Commit()
    {
        var text = (Editor.Text ?? string.Empty).Trim();
        // 去掉后缀和非数字（用户可能把 "3072 MB" 整串粘进来）
        var digits = new string(text.TakeWhile(char.IsDigit).ToArray());
        var parsed = int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Coerce(value)
            : Value;      // 空串 / 全是垃圾 → 退回原值，不报错
        if (parsed != Value)
        {
            SetCurrentValue(ValueProperty, parsed);
            ValueChanged?.Invoke(this, parsed);
        }
        RefreshText();
    }

    private int Coerce(int value) => Math.Clamp(value, Minimum, Maximum);

    /// <summary>程序设值（不回触发 <see cref="ValueChanged"/>）。</summary>
    public void SetValueSilently(int value)
    {
        _updating = true;
        try
        {
            SetCurrentValue(ValueProperty, Coerce(value));
        }
        finally
        {
            _updating = false;
        }
        RefreshText();
    }

    /// <summary>这次改动是不是"程序自己在设值"。</summary>
    public bool IsProgrammatic => _updating;
}
