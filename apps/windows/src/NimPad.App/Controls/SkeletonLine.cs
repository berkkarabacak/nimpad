using System.Windows;
using System.Windows.Media;

namespace NimPad.App.Controls;

/// <summary>
/// A loading placeholder bar drawn at the final size of the text it stands in for (deck page 29: 300-600 ms rule).
/// Static (no shimmer), fully rounded, filled with <see cref="Fill"/> (Hover by default via its style).
/// Set Width (or let it stretch) and Height (default 12 from the style).
/// </summary>
public sealed class SkeletonLine : FrameworkElement
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(SkeletonLine),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var fill = Fill;
        var size = RenderSize;
        if (fill is null || size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        var radius = size.Height / 2;
        drawingContext.DrawRoundedRectangle(fill, null, new Rect(size), radius, radius);
    }
}
