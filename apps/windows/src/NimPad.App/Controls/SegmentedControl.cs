using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NimPad.App.Controls;

/// <summary>
/// A 2 to 4 option segmented control (Theme: System | Light | Dark, Tab size: 2 | 4 | 8), deck page 25.
/// A single-selection <see cref="ListBox"/>: bind <c>ItemsSource</c> + <c>SelectedItem</c>/<c>SelectedValue</c>
/// (with <c>SelectedValuePath</c>), or declare <see cref="SegmentedControlItem"/> children. One tab stop; the arrow
/// keys, Home and End move the selection.
/// </summary>
public class SegmentedControl : ListBox
{
    static SegmentedControl()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(SegmentedControl), new FrameworkPropertyMetadata(typeof(SegmentedControl)));
        SelectionModeProperty.OverrideMetadata(
            typeof(SegmentedControl), new FrameworkPropertyMetadata(SelectionMode.Single));
    }

    protected override DependencyObject GetContainerForItemOverride() => new SegmentedControlItem();

    protected override bool IsItemItsOwnContainerOverride(object item) => item is SegmentedControlItem;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var count = Items.Count;
        if (count == 0 || e.Handled)
        {
            base.OnKeyDown(e);
            return;
        }

        var index = SelectedIndex;
        var target = e.Key switch
        {
            Key.Left or Key.Up => Math.Max(0, index - 1),
            Key.Right or Key.Down => index < 0 ? 0 : Math.Min(count - 1, index + 1),
            Key.Home => 0,
            Key.End => count - 1,
            _ => -2,
        };
        if (target == -2)
        {
            base.OnKeyDown(e);
            return;
        }

        e.Handled = true;
        if (target != index)
        {
            SetCurrentValue(SelectedIndexProperty, target);
        }

        if (ItemContainerGenerator.ContainerFromIndex(target) is UIElement container)
        {
            container.Focus();
        }
    }
}

/// <summary>One segment of a <see cref="SegmentedControl"/>.</summary>
public class SegmentedControlItem : ListBoxItem
{
    static SegmentedControlItem()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(SegmentedControlItem), new FrameworkPropertyMetadata(typeof(SegmentedControlItem)));
    }
}
