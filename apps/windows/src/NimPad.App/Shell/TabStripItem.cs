using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NimPad.Presentation.Tabs;

namespace NimPad.App.Shell;

/// <summary>
/// One tab of the <see cref="TabStrip"/> (design page 24): default, hover, selected, keyboard focus, pinned, waiting to
/// save (accent dot), save failed (Err dot), loading (muted title + spinner), renaming (inline text box) and dragging
/// (lifted). Its look lives in the tab strip's container style; this class adds the state the template needs and the
/// mouse behavior (middle-click closes, double-click renames).
/// </summary>
public class TabStripItem : ListBoxItem
{
    /// <summary>The <see cref="Index"/> property.</summary>
    public static readonly DependencyProperty IndexProperty = DependencyProperty.Register(
        nameof(Index), typeof(int), typeof(TabStripItem), new FrameworkPropertyMetadata(-1, OnIndexChanged));

    /// <summary>The <see cref="ShowSeparator"/> property.</summary>
    public static readonly DependencyProperty ShowSeparatorProperty = DependencyProperty.Register(
        nameof(ShowSeparator), typeof(bool), typeof(TabStripItem), new FrameworkPropertyMetadata(false));

    /// <summary>The <see cref="IsDragSource"/> property.</summary>
    public static readonly DependencyProperty IsDragSourceProperty = DependencyProperty.Register(
        nameof(IsDragSource), typeof(bool), typeof(TabStripItem), new FrameworkPropertyMetadata(false));

    /// <summary>The <see cref="IsPinnedTab"/> property (bound to the tab's IsPinned by the container style).</summary>
    public static readonly DependencyProperty IsPinnedTabProperty = DependencyProperty.Register(
        nameof(IsPinnedTab), typeof(bool), typeof(TabStripItem),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

    /// <summary>The <see cref="IsRenamingTab"/> property (bound to the tab's IsRenaming by the container style).</summary>
    public static readonly DependencyProperty IsRenamingTabProperty = DependencyProperty.Register(
        nameof(IsRenamingTab), typeof(bool), typeof(TabStripItem), new FrameworkPropertyMetadata(false, OnIsRenamingTabChanged));

    private TextBox? _renameBox;

    /// <summary>The tab's visual position (0-based); drives the <c>Tab_n</c> / <c>TabClose_n</c> automation IDs.</summary>
    public int Index
    {
        get => (int)GetValue(IndexProperty);
        set => SetValue(IndexProperty, value);
    }

    /// <summary>True when the 1 x 16 separator shows at the right edge (between two unselected, unhovered tabs).</summary>
    public bool ShowSeparator
    {
        get => (bool)GetValue(ShowSeparatorProperty);
        set => SetValue(ShowSeparatorProperty, value);
    }

    /// <summary>True while the tab is being dragged (lifted with a flyout shadow).</summary>
    public bool IsDragSource
    {
        get => (bool)GetValue(IsDragSourceProperty);
        set => SetValue(IsDragSourceProperty, value);
    }

    /// <summary>True for a pinned tab (104 px, pin icon, no close button).</summary>
    public bool IsPinnedTab
    {
        get => (bool)GetValue(IsPinnedTabProperty);
        set => SetValue(IsPinnedTabProperty, value);
    }

    /// <summary>True while the inline rename box shows.</summary>
    public bool IsRenamingTab
    {
        get => (bool)GetValue(IsRenamingTabProperty);
        set => SetValue(IsRenamingTabProperty, value);
    }

    /// <summary>The tab's view model.</summary>
    public TabViewModel? Tab => DataContext as TabViewModel;

    internal TabStrip? Owner => ItemsControl.ItemsControlFromItemContainer(this) as TabStrip;

    /// <inheritdoc/>
    public override void OnApplyTemplate()
    {
        if (_renameBox is not null)
        {
            _renameBox.LostKeyboardFocus -= OnRenameBoxLostFocus;
        }

        base.OnApplyTemplate();
        _renameBox = GetTemplateChild("PART_RenameBox") as TextBox;
        if (_renameBox is not null)
        {
            _renameBox.LostKeyboardFocus += OnRenameBoxLostFocus;
        }
    }

    /// <inheritdoc/>
    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        Owner?.UpdateSeparators();
    }

    /// <inheritdoc/>
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        Owner?.UpdateSeparators();
    }

    /// <inheritdoc/>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            // Closed on release, like a click.
            e.Handled = true;
            return;
        }

        base.OnMouseDown(e);
    }

    /// <inheritdoc/>
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && IsMouseOver && Tab is { } tab)
        {
            e.Handled = true;
            Owner?.CloseTab(tab);
            return;
        }

        base.OnMouseUp(e);
    }

    /// <inheritdoc/>
    protected override void OnMouseDoubleClick(MouseButtonEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.ChangedButton == MouseButton.Left && Tab is { CanRename: true } tab && !IsWithinCloseButton(e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
            Owner?.RenameTab(tab);
        }
    }

    /// <summary>True when <paramref name="source"/> is inside this tab's close button.</summary>
    internal bool IsWithinCloseButton(DependencyObject? source)
    {
        var close = GetTemplateChild("PART_CloseButton");
        for (var node = source; node is not null && node != this; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node == close)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when <paramref name="source"/> is inside the inline rename box.</summary>
    internal bool IsWithinRenameBox(DependencyObject? source)
    {
        for (var node = source; node is not null && node != this; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node == _renameBox)
            {
                return true;
            }
        }

        return false;
    }

    private static void OnIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TabStripItem)d;
        var index = (int)e.NewValue;
        AutomationProperties.SetAutomationId(item, index < 0 ? string.Empty : "Tab_" + index.ToString(CultureInfo.InvariantCulture));
    }

    private static void OnIsRenamingTabChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TabStripItem)d;
        if ((bool)e.NewValue)
        {
            // The box becomes visible with this change; focus it once it is laid out.
            item.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (item.IsRenamingTab && item._renameBox is { } box)
                {
                    box.Focus();
                    box.SelectAll();
                }
            }));
        }
        else if (item._renameBox is { IsKeyboardFocusWithin: true })
        {
            item.Focus();
        }
    }

    private void OnRenameBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Clicking elsewhere keeps the typed name, as in Windows Explorer; a rejected name keeps the box open.
        if (Tab is { IsRenaming: true } tab && tab.CommitRenameCommand.CanExecute(null))
        {
            tab.CommitRenameCommand.Execute(null);
        }
    }
}
