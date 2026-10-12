using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace NimPad.App.Controls;

/// <summary>
/// Draws one icon geometry from Themes/Icons.xaml. The geometry is designed on a 24 x 24 grid and scaled to
/// <see cref="Size"/>; strokes keep <see cref="StrokeThickness"/> DIPs at any size, with round caps and joins.
/// The color comes from <see cref="Foreground"/>, which inherits like text, so an icon inside a button follows the
/// button's state. Cheap: no template, no visual children, one cached pen.
/// </summary>
/// <example><c>&lt;c:Icon Data="{StaticResource Search}" Foreground="{DynamicResource Text3Brush}" /&gt;</c></example>
public sealed class Icon : FrameworkElement
{
    /// <summary>The design grid every NimPad icon is drawn on.</summary>
    public const double GridSize = 24;

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(Icon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(Icon),
        new FrameworkPropertyMetadata(
            16.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            OnPenAffectingChanged),
        IsValidLength);

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(Icon),
        new FrameworkPropertyMetadata(
            SystemColors.ControlTextBrush,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender,
            OnPenAffectingChanged));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Icon),
        new FrameworkPropertyMetadata(1.25, FrameworkPropertyMetadataOptions.AffectsRender, OnPenAffectingChanged),
        IsValidLength);

    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(
        nameof(Filled), typeof(bool), typeof(Icon),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private Pen? _pen;

    /// <summary>Icon geometry on the 24 x 24 grid (e.g. <c>{StaticResource Close}</c>).</summary>
    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <summary>Rendered width and height in DIPs (default 16).</summary>
    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>Icon color; inherited from the parent's TextElement.Foreground (e.g. the button's Foreground).</summary>
    public Brush? Foreground
    {
        get => (Brush?)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>Stroke width in DIPs at the rendered size (default 1.25).</summary>
    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    /// <summary>Fill the geometry instead of stroking it (AppMark, AppMarkTile, AppMarkLines).</summary>
    public bool Filled
    {
        get => (bool)GetValue(FilledProperty);
        set => SetValue(FilledProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    protected override void OnRender(DrawingContext drawingContext)
    {
        var data = Data;
        var brush = Foreground;
        var size = Size;
        if (data is null || brush is null || size <= 0)
        {
            return;
        }

        var scale = size / GridSize;
        var offsetX = Math.Round((RenderSize.Width - size) / 2);
        var offsetY = Math.Round((RenderSize.Height - size) / 2);
        drawingContext.PushTransform(new MatrixTransform(scale, 0, 0, scale, offsetX, offsetY));
        if (Filled)
        {
            drawingContext.DrawGeometry(brush, null, data);
        }
        else
        {
            drawingContext.DrawGeometry(null, GetPen(brush, scale), data);
        }

        drawingContext.Pop();
    }

    private Pen GetPen(Brush brush, double scale)
    {
        // The pen lives in grid space, so divide by the scale to keep StrokeThickness in DIPs.
        var thickness = StrokeThickness / scale;
        if (_pen is not null && ReferenceEquals(_pen.Brush, brush) && _pen.Thickness.Equals(thickness))
        {
            return _pen;
        }

        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        if (pen.CanFreeze)
        {
            pen.Freeze();
        }

        _pen = pen;
        return pen;
    }

    private static void OnPenAffectingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((Icon)d)._pen = null;

    private static bool IsValidLength(object value) => value is double d && d >= 0 && !double.IsNaN(d) &&
        !double.IsPositiveInfinity(d);
}
