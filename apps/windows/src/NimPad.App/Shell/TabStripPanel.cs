using System.Windows;
using System.Windows.Controls;

namespace NimPad.App.Shell;

/// <summary>
/// The tab strip's items panel: tabs side by side, pinned tabs at <see cref="PinnedTabWidth"/>, the others sharing the
/// strip's width equally between <see cref="MaxTabWidth"/> and <see cref="MinTabWidth"/> (design page 28: tabs shrink
/// from 172 toward 96, then the strip scrolls). The available width comes from the owning <see cref="TabStrip"/>
/// (the panel itself sits in a scroll viewer and is measured with infinite width).
/// </summary>
public class TabStripPanel : Panel
{
    /// <summary>The widest tab (design: 172).</summary>
    public const double MaxTabWidth = 172;

    /// <summary>The narrowest tab before the strip scrolls (design: 96).</summary>
    public const double MinTabWidth = 96;

    /// <summary>A pinned tab's width (design: 104).</summary>
    public const double PinnedTabWidth = 104;

    /// <summary>The tab height (design: 34).</summary>
    public const double TabHeight = 34;

    /// <summary>The width every unpinned tab got in the last measure.</summary>
    public double TabWidth { get; private set; } = MaxTabWidth;

    /// <summary>
    /// The width of an unpinned tab for <paramref name="available"/> pixels, <paramref name="pinned"/> pinned and
    /// <paramref name="unpinned"/> other tabs.
    /// </summary>
    public static double ComputeTabWidth(double available, int pinned, int unpinned)
    {
        if (unpinned <= 0 || !double.IsFinite(available))
        {
            return MaxTabWidth;
        }

        var share = (available - (pinned * PinnedTabWidth)) / unpinned;
        return Math.Floor(Math.Clamp(share, MinTabWidth, MaxTabWidth));
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this) as TabStrip;
        owner?.RegisterPanel(this);
        var available = owner?.AvailableTabsWidth ?? availableSize.Width;
        var children = InternalChildren;
        var pinned = 0;
        foreach (UIElement child in children)
        {
            if (child is TabStripItem { IsPinnedTab: true })
            {
                pinned++;
            }
        }

        var unpinned = children.Count - pinned;
        TabWidth = ComputeTabWidth(available, pinned, unpinned);
        var total = 0.0;
        var height = 0.0;
        foreach (UIElement child in children)
        {
            var width = WidthOf(child);
            child.Measure(new Size(width, TabHeight));
            total += width;
            height = Math.Max(height, child.DesiredSize.Height);
        }

        owner?.SetOverflowing(double.IsFinite(available) && total > available + 0.5);
        return new Size(total, Math.Max(height, TabHeight));
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            var width = WidthOf(child);
            child.Arrange(new Rect(x, Math.Max(0, finalSize.Height - TabHeight), width, TabHeight));
            x += width;
        }

        return finalSize;
    }

    private double WidthOf(UIElement child) => child is TabStripItem { IsPinnedTab: true } ? PinnedTabWidth : TabWidth;
}

/// <summary>
/// Lays out the title bar's tab area: the tab strip, then the new-note button right after the last tab. The strip gets
/// at most the width left after the button and <see cref="DragReserve"/> (so part of the title bar always stays free to
/// drag the window); beyond that it scrolls.
/// </summary>
public class TitleTabsLayout : Panel
{
    /// <summary>The <see cref="DragReserve"/> property.</summary>
    public static readonly DependencyProperty DragReserveProperty = DependencyProperty.Register(
        nameof(DragReserve), typeof(double), typeof(TitleTabsLayout), new FrameworkPropertyMetadata(46.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The gap between the strip and the button.</summary>
    public const double ButtonGap = 4;

    /// <summary>Title bar width kept free for dragging the window (default 46, one window control).</summary>
    public double DragReserve
    {
        get => (double)GetValue(DragReserveProperty);
        set => SetValue(DragReserveProperty, value);
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count == 0)
        {
            return default;
        }

        var strip = InternalChildren[0];
        var button = InternalChildren.Count > 1 ? InternalChildren[1] : null;
        button?.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        var buttonWidth = button is null || button.Visibility == Visibility.Collapsed ? 0 : button.DesiredSize.Width + ButtonGap;
        var stripMax = double.IsFinite(availableSize.Width) ? Math.Max(0, availableSize.Width - buttonWidth - DragReserve) : double.PositiveInfinity;
        strip.Measure(new Size(stripMax, availableSize.Height));
        var height = Math.Max(strip.DesiredSize.Height, button?.DesiredSize.Height ?? 0);
        return new Size(Math.Min(strip.DesiredSize.Width, stripMax) + buttonWidth, double.IsFinite(availableSize.Height) ? availableSize.Height : height);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count == 0)
        {
            return finalSize;
        }

        var strip = InternalChildren[0];
        var button = InternalChildren.Count > 1 ? InternalChildren[1] : null;
        var buttonWidth = button is null || button.Visibility == Visibility.Collapsed ? 0 : button.DesiredSize.Width + ButtonGap;
        var stripWidth = Math.Max(0, Math.Min(strip.DesiredSize.Width, finalSize.Width - buttonWidth));
        strip.Arrange(new Rect(0, 0, stripWidth, finalSize.Height));
        if (button is not null)
        {
            var size = button.DesiredSize;
            var top = TabStripPanel.TabHeight > finalSize.Height ? 0 : (finalSize.Height - TabStripPanel.TabHeight) + ((TabStripPanel.TabHeight - size.Height) / 2);
            button.Arrange(new Rect(stripWidth + ButtonGap, Math.Max(0, top), size.Width, size.Height));
        }

        return finalSize;
    }
}
