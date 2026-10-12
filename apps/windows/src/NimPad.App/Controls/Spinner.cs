using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace NimPad.App.Controls;

/// <summary>
/// The 16 px busy arc (tabs loading, busy buttons, "&gt; 600 ms" loading rule on deck page 29). It rotates only
/// while it is loaded, visible and <see cref="IsActive"/>, and stays still when Windows animations are turned off.
/// Color: <see cref="Foreground"/> (Accent by default via its style; set Text2Brush inside busy buttons).
/// </summary>
public sealed class Spinner : FrameworkElement
{
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(Spinner),
        new FrameworkPropertyMetadata(
            16.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            OnPenAffectingChanged),
        value => value is double d && d >= 0 && double.IsFinite(d));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(Spinner),
        new FrameworkPropertyMetadata(
            SystemColors.ControlTextBrush,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender,
            OnPenAffectingChanged));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Spinner),
        new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsRender, OnPenAffectingChanged),
        value => value is double d && d >= 0 && double.IsFinite(d));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(Spinner),
        new FrameworkPropertyMetadata(true, (d, _) => ((Spinner)d).UpdateAnimation()));

    private static readonly Duration TurnDuration = new(TimeSpan.FromMilliseconds(900));

    private readonly RotateTransform _rotation = new();
    private Pen? _pen;
    private bool _spinning;

    public Spinner()
    {
        RenderTransform = _rotation;
        RenderTransformOrigin = new Point(0.5, 0.5);
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => UpdateAnimation();
        IsVisibleChanged += (_, _) => UpdateAnimation();
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public Brush? Foreground
    {
        get => (Brush?)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>Arc width in DIPs (default 1.5).</summary>
    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    /// <summary>Set false to keep the spinner in the tree without animating it.</summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>True while the rotation storyboard is running (for tests and diagnostics).</summary>
    public bool IsSpinning => _spinning;

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    protected override void OnRender(DrawingContext drawingContext)
    {
        var brush = Foreground;
        var size = Size;
        if (brush is null || size <= 0)
        {
            return;
        }

        // A 300 degree arc on a circle inset by half the pen, centered in the render box.
        var pen = _pen ??= CreatePen(brush, StrokeThickness);
        var radius = Math.Max(0, (size - pen.Thickness) / 2);
        var center = new Point(RenderSize.Width / 2, RenderSize.Height / 2);
        var start = new Point(center.X, center.Y - radius);
        var endAngle = 300 * Math.PI / 180;
        var end = new Point(center.X + (radius * Math.Sin(endAngle)), center.Y - (radius * Math.Cos(endAngle)));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.ArcTo(end, new Size(radius, radius), 0, true, SweepDirection.Clockwise, true, true);
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(null, pen, geometry);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SpinnerAutomationPeer(this);

    private static Pen CreatePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (pen.CanFreeze)
        {
            pen.Freeze();
        }

        return pen;
    }

    private static void OnPenAffectingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((Spinner)d)._pen = null;

    private void UpdateAnimation()
    {
        var run = IsActive && IsLoaded && IsVisible && SystemParameters.ClientAreaAnimation;
        if (run == _spinning)
        {
            return;
        }

        _spinning = run;
        if (run)
        {
            var turn = new DoubleAnimation(0, 360, TurnDuration) { RepeatBehavior = RepeatBehavior.Forever };
            turn.Freeze();
            _rotation.BeginAnimation(RotateTransform.AngleProperty, turn);
        }
        else
        {
            // Removing the clock stops the per-frame work entirely.
            _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }

    private sealed class SpinnerAutomationPeer(Spinner owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() =>
            AutomationControlType.ProgressBar;

        protected override string GetClassNameCore() => nameof(Spinner);
    }
}
