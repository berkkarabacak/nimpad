using System.Windows;
using System.Windows.Controls;

namespace NimPad.App.Shell;

/// <summary>
/// Lays its children out in up to <see cref="Columns"/> columns, in order, top to bottom and then left to right,
/// choosing the split that makes the tallest column as short as possible (the keyboard shortcuts dialog's groups).
/// Below <see cref="MinColumnWidth"/> per column it uses fewer columns.
/// </summary>
public class BalancedColumnsPanel : Panel
{
    /// <summary>The <see cref="Columns"/> property.</summary>
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(BalancedColumnsPanel),
        new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure), value => value is int n && n >= 1);

    /// <summary>The <see cref="ColumnGap"/> property.</summary>
    public static readonly DependencyProperty ColumnGapProperty = DependencyProperty.Register(
        nameof(ColumnGap), typeof(double), typeof(BalancedColumnsPanel),
        new FrameworkPropertyMetadata(32.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The <see cref="MinColumnWidth"/> property.</summary>
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(BalancedColumnsPanel),
        new FrameworkPropertyMetadata(260.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private int _effectiveColumns = 1;
    private int[] _columnStarts = [0];

    /// <summary>The most columns (default 2).</summary>
    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    /// <summary>The gap between columns (default 32).</summary>
    public double ColumnGap
    {
        get => (double)GetValue(ColumnGapProperty);
        set => SetValue(ColumnGapProperty, value);
    }

    /// <summary>The narrowest column before the panel drops a column (default 260).</summary>
    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren;
        var columns = Columns;
        if (double.IsFinite(availableSize.Width))
        {
            while (columns > 1 && (availableSize.Width - ((columns - 1) * ColumnGap)) / columns < MinColumnWidth)
            {
                columns--;
            }
        }

        columns = Math.Max(1, Math.Min(columns, Math.Max(1, children.Count)));
        var columnWidth = double.IsFinite(availableSize.Width)
            ? Math.Max(0, (availableSize.Width - ((columns - 1) * ColumnGap)) / columns)
            : double.PositiveInfinity;
        var heights = new double[children.Count];
        var widest = 0.0;
        for (var i = 0; i < children.Count; i++)
        {
            children[i].Measure(new Size(columnWidth, double.PositiveInfinity));
            heights[i] = children[i].DesiredSize.Height;
            widest = Math.Max(widest, children[i].DesiredSize.Width);
        }

        _effectiveColumns = columns;
        _columnStarts = Split(heights, columns);
        var tallest = 0.0;
        for (var c = 0; c < _columnStarts.Length; c++)
        {
            var end = c + 1 < _columnStarts.Length ? _columnStarts[c + 1] : heights.Length;
            var sum = 0.0;
            for (var i = _columnStarts[c]; i < end; i++)
            {
                sum += heights[i];
            }

            tallest = Math.Max(tallest, sum);
        }

        var width = double.IsFinite(columnWidth) ? availableSize.Width : (widest * columns) + ((columns - 1) * ColumnGap);
        return new Size(width, tallest);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren;
        var columns = _effectiveColumns;
        var columnWidth = Math.Max(0, (finalSize.Width - ((columns - 1) * ColumnGap)) / columns);
        for (var c = 0; c < _columnStarts.Length; c++)
        {
            var end = c + 1 < _columnStarts.Length ? _columnStarts[c + 1] : children.Count;
            var y = 0.0;
            for (var i = _columnStarts[c]; i < end && i < children.Count; i++)
            {
                var height = children[i].DesiredSize.Height;
                children[i].Arrange(new Rect(c * (columnWidth + ColumnGap), y, columnWidth, height));
                y += height;
            }
        }

        return finalSize;
    }

    // Contiguous split of the heights into at most `columns` runs minimizing the tallest run (small n: brute force
    // over the split points for 2 columns, greedy for more).
    private static int[] Split(double[] heights, int columns)
    {
        if (columns <= 1 || heights.Length <= 1)
        {
            return [0];
        }

        if (columns == 2)
        {
            var total = heights.Sum();
            var best = 1;
            var bestTallest = double.MaxValue;
            var left = 0.0;
            for (var split = 1; split < heights.Length; split++)
            {
                left += heights[split - 1];
                var tallest = Math.Max(left, total - left);
                if (tallest < bestTallest)
                {
                    bestTallest = tallest;
                    best = split;
                }
            }

            return [0, best];
        }

        var target = heights.Sum() / columns;
        var starts = new List<int> { 0 };
        var run = 0.0;
        for (var i = 0; i < heights.Length; i++)
        {
            if (run > 0 && run + (heights[i] / 2) > target && starts.Count < columns)
            {
                starts.Add(i);
                run = 0;
            }

            run += heights[i];
        }

        return [.. starts];
    }
}
