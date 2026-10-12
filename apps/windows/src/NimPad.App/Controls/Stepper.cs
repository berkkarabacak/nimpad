using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace NimPad.App.Controls;

/// <summary>
/// The "− 14 px +" stepper (font size), deck page 25. One tab stop; Up/Right increase, Down/Left decrease,
/// PageUp/PageDown move by 5 steps, Home/End jump to Minimum/Maximum. <see cref="Value"/> binds two-way by default
/// and is clamped to [Minimum, Maximum]. Exposed to UI Automation with the RangeValue pattern.
/// </summary>
[TemplatePart(Name = PartDecreaseButton, Type = typeof(ButtonBase))]
[TemplatePart(Name = PartIncreaseButton, Type = typeof(ButtonBase))]
public class Stepper : Control
{
    public const string PartDecreaseButton = "PART_DecreaseButton";
    public const string PartIncreaseButton = "PART_IncreaseButton";

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(Stepper),
        new FrameworkPropertyMetadata(
            0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged, CoerceValueToRange),
        IsFinite);

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(Stepper),
        new FrameworkPropertyMetadata(0.0, OnRangeChanged), IsFinite);

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Stepper),
        new FrameworkPropertyMetadata(100.0, OnRangeChanged), IsFinite);

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(double), typeof(Stepper), new FrameworkPropertyMetadata(1.0), IsPositive);

    public static readonly DependencyProperty SuffixProperty = DependencyProperty.Register(
        nameof(Suffix), typeof(string), typeof(Stepper), new FrameworkPropertyMetadata(null, OnDisplayChanged));

    private static readonly DependencyPropertyKey DisplayTextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(DisplayText), typeof(string), typeof(Stepper), new FrameworkPropertyMetadata("0"));

    public static readonly DependencyProperty DisplayTextProperty = DisplayTextPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey CanDecreasePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(CanDecrease), typeof(bool), typeof(Stepper), new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty CanDecreaseProperty = CanDecreasePropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey CanIncreasePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(CanIncrease), typeof(bool), typeof(Stepper), new FrameworkPropertyMetadata(true));

    public static readonly DependencyProperty CanIncreaseProperty = CanIncreasePropertyKey.DependencyProperty;

    public static readonly RoutedEvent ValueChangedEvent = EventManager.RegisterRoutedEvent(
        nameof(ValueChanged), RoutingStrategy.Bubble, typeof(RoutedPropertyChangedEventHandler<double>),
        typeof(Stepper));

    private ButtonBase? _decrease;
    private ButtonBase? _increase;

    static Stepper()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(Stepper), new FrameworkPropertyMetadata(typeof(Stepper)));
        FocusableProperty.OverrideMetadata(typeof(Stepper), new FrameworkPropertyMetadata(true));
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>Amount added or removed by one press (default 1).</summary>
    public double Step
    {
        get => (double)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    /// <summary>Unit shown after the value, e.g. "px" renders "14 px".</summary>
    public string? Suffix
    {
        get => (string?)GetValue(SuffixProperty);
        set => SetValue(SuffixProperty, value);
    }

    /// <summary>The formatted value with its suffix, as displayed.</summary>
    public string DisplayText => (string)GetValue(DisplayTextProperty);

    public bool CanDecrease => (bool)GetValue(CanDecreaseProperty);

    public bool CanIncrease => (bool)GetValue(CanIncreaseProperty);

    public event RoutedPropertyChangedEventHandler<double> ValueChanged
    {
        add => AddHandler(ValueChangedEvent, value);
        remove => RemoveHandler(ValueChangedEvent, value);
    }

    public void Increase() => SetCurrentValue(ValueProperty, Value + Step);

    public void Decrease() => SetCurrentValue(ValueProperty, Value - Step);

    public override void OnApplyTemplate()
    {
        if (_decrease is not null)
        {
            _decrease.Click -= OnDecreaseClick;
        }

        if (_increase is not null)
        {
            _increase.Click -= OnIncreaseClick;
        }

        base.OnApplyTemplate();
        _decrease = GetTemplateChild(PartDecreaseButton) as ButtonBase;
        _increase = GetTemplateChild(PartIncreaseButton) as ButtonBase;
        if (_decrease is not null)
        {
            _decrease.Click += OnDecreaseClick;
        }

        if (_increase is not null)
        {
            _increase.Click += OnIncreaseClick;
        }

        UpdateDerived();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }

        double? target = e.Key switch
        {
            Key.Up or Key.Right => Value + Step,
            Key.Down or Key.Left => Value - Step,
            Key.PageUp => Value + (Step * 5),
            Key.PageDown => Value - (Step * 5),
            Key.Home => Minimum,
            Key.End => Maximum,
            _ => null,
        };
        if (target is double value)
        {
            SetCurrentValue(ValueProperty, value);
            e.Handled = true;
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsKeyboardFocusWithin)
        {
            Focus();
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new StepperAutomationPeer(this);

    private void OnDecreaseClick(object sender, RoutedEventArgs e) => Decrease();

    private void OnIncreaseClick(object sender, RoutedEventArgs e) => Increase();

    private void UpdateDerived()
    {
        var value = Value;
        var number = value.ToString("0.##", CultureInfo.CurrentCulture);
        var suffix = Suffix;
        SetValue(DisplayTextPropertyKey, string.IsNullOrEmpty(suffix) ? number : number + " " + suffix);
        SetValue(CanDecreasePropertyKey, value > Minimum);
        SetValue(CanIncreasePropertyKey, value < Maximum);
    }

    private static object CoerceValueToRange(DependencyObject d, object? baseValue)
    {
        var stepper = (Stepper)d;
        var value = baseValue is double v ? v : 0.0;
        var min = stepper.Minimum;
        var max = Math.Max(min, stepper.Maximum);
        return Math.Clamp(value, min, max);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var stepper = (Stepper)d;
        stepper.UpdateDerived();
        var oldValue = (double)e.OldValue;
        var newValue = (double)e.NewValue;
        if (UIElementAutomationPeer.FromElement(stepper) is StepperAutomationPeer peer)
        {
            peer.RaiseValueChanged(oldValue, newValue);
        }

        stepper.RaiseEvent(new RoutedPropertyChangedEventArgs<double>(oldValue, newValue, ValueChangedEvent));
    }

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var stepper = (Stepper)d;
        stepper.CoerceValue(ValueProperty);
        stepper.UpdateDerived();
    }

    private static void OnDisplayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((Stepper)d).UpdateDerived();

    private static bool IsFinite(object value) => value is double d && double.IsFinite(d);

    private static bool IsPositive(object value) => value is double d && double.IsFinite(d) && d > 0;

    private sealed class StepperAutomationPeer(Stepper owner)
        : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        private Stepper Stepper => (Stepper)Owner;

        public bool IsReadOnly => !Stepper.IsEnabled;

        public double LargeChange => Stepper.Step * 5;

        public double Maximum => Stepper.Maximum;

        public double Minimum => Stepper.Minimum;

        public double SmallChange => Stepper.Step;

        public double Value => Stepper.Value;

        public void SetValue(double value)
        {
            if (!Stepper.IsEnabled)
            {
                throw new ElementNotEnabledException();
            }

            Stepper.SetCurrentValue(ValueProperty, value);
        }

        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.RangeValue ? this : base.GetPattern(patternInterface);

        public void RaiseValueChanged(double oldValue, double newValue) =>
            RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, oldValue, newValue);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Spinner;

        protected override string GetClassNameCore() => nameof(Stepper);

        protected override string GetItemStatusCore() => Stepper.DisplayText;
    }
}
