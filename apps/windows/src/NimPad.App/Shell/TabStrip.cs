using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NimPad.Presentation.Shell;
using NimPad.Presentation.Tabs;

namespace NimPad.App.Shell;

/// <summary>
/// The title bar's tab strip (design pages 24, 10 and 27): a single-selection list of <see cref="TabViewModel"/>s with
/// one tab stop. Keyboard: Left/Right/Home/End move and select, Enter sends focus to the editor, F2 renames, Delete
/// closes, Ctrl+Shift+Left/Right move the tab, Menu or Shift+F10 opens the tab menu. Mouse: click selects (focus goes
/// back to the editor), middle-click closes, double-click renames, drag reorders (an accent marker shows the drop
/// place; pinned tabs stay in front), the wheel scrolls. Tabs shrink from 172 to 96 px, then the strip scrolls
/// (chevrons appear) and keeps the selected tab in view. DataContext: the <see cref="ShellViewModel"/>.
/// </summary>
public class TabStrip : ListBox
{
    /// <summary>The <see cref="IsOverflowing"/> property.</summary>
    public static readonly DependencyProperty IsOverflowingProperty = DependencyProperty.Register(
        nameof(IsOverflowing), typeof(bool), typeof(TabStrip), new FrameworkPropertyMetadata(false));

    private ScrollViewer? _scrollViewer;
    private TabStripPanel? _itemsPanel;
    private Rectangle? _dropMarker;
    private ButtonBase? _scrollLeft;
    private ButtonBase? _scrollRight;
    private TabStripItem? _pressed;
    private Point _pressPoint;
    private bool _dragging;
    private double _dragOffset;
    private int _dropIndex = -1;
    private bool _separatorUpdateQueued;
    private bool _renumberQueued;

    static TabStrip()
    {
        SelectionModeProperty.OverrideMetadata(typeof(TabStrip), new FrameworkPropertyMetadata(SelectionMode.Single));
    }

    /// <summary>Creates the strip.</summary>
    public TabStrip()
    {
        ((INotifyCollectionChanged)Items).CollectionChanged += OnItemsCollectionChanged;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Once);
        KeyboardNavigation.SetDirectionalNavigation(this, KeyboardNavigationMode.None);
    }

    /// <summary>Raised when keyboard focus should go to the editor (Enter on a tab, a mouse click on a tab).</summary>
    public event EventHandler? EditorFocusRequested;

    /// <summary>True when the tabs do not fit even at their narrowest: the strip scrolls and shows chevrons.</summary>
    public bool IsOverflowing
    {
        get => (bool)GetValue(IsOverflowingProperty);
        private set => SetValue(IsOverflowingProperty, value);
    }

    /// <summary>The width the strip may use, set while measuring (the panel sizes the tabs from it).</summary>
    public double AvailableTabsWidth { get; private set; } = double.PositiveInfinity;

    /// <summary>The window's view model.</summary>
    public ShellViewModel? Shell => DataContext as ShellViewModel;

    /// <summary>The container of <paramref name="tab"/>, or <see langword="null"/>.</summary>
    public TabStripItem? ContainerOf(TabViewModel? tab) =>
        tab is null ? null : ItemContainerGenerator.ContainerFromItem(tab) as TabStripItem;

    /// <summary>Moves keyboard focus to the selected tab (F6, or after a keyboard action).</summary>
    /// <returns><see langword="true"/> when a tab took focus.</returns>
    public bool FocusSelectedTab()
    {
        var container = ContainerOf(SelectedItem as TabViewModel);
        if (container is null)
        {
            UpdateLayout();
            container = ContainerOf(SelectedItem as TabViewModel);
        }

        return container is not null && container.Focus();
    }

    /// <inheritdoc/>
    public override void OnApplyTemplate()
    {
        if (_scrollLeft is not null)
        {
            _scrollLeft.Click -= OnScrollLeftClick;
        }

        if (_scrollRight is not null)
        {
            _scrollRight.Click -= OnScrollRightClick;
        }

        base.OnApplyTemplate();
        _scrollViewer = GetTemplateChild("PART_ScrollViewer") as ScrollViewer;
        _dropMarker = GetTemplateChild("PART_DropMarker") as Rectangle;
        _scrollLeft = GetTemplateChild("PART_ScrollLeft") as ButtonBase;
        _scrollRight = GetTemplateChild("PART_ScrollRight") as ButtonBase;
        if (_scrollLeft is not null)
        {
            _scrollLeft.Click += OnScrollLeftClick;
        }

        if (_scrollRight is not null)
        {
            _scrollRight.Click += OnScrollRightClick;
        }
    }

    internal void RegisterPanel(TabStripPanel panel) => _itemsPanel = panel;

    internal void SetOverflowing(bool value)
    {
        if (IsOverflowing != value)
        {
            // Changing a template part's visibility during measure is fine: the strip measures again.
            Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => IsOverflowing = value));
        }
    }

    internal void CloseTab(TabViewModel tab)
    {
        if (Shell?.Tabs.CloseTabCommand is { } command && command.CanExecute(tab))
        {
            command.Execute(tab);
        }
    }

    internal void RenameTab(TabViewModel tab)
    {
        if (Shell?.Tabs.RenameTabCommand is { } command && command.CanExecute(tab))
        {
            command.Execute(tab);
        }
    }

    /// <summary>Shows the 1 x 16 separators between unselected, unhovered neighbours (none between pinned tabs).</summary>
    internal void UpdateSeparators()
    {
        if (_separatorUpdateQueued)
        {
            return;
        }

        _separatorUpdateQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _separatorUpdateQueued = false;
            var count = Items.Count;
            for (var i = 0; i < count; i++)
            {
                if (ItemContainerGenerator.ContainerFromIndex(i) is not TabStripItem item)
                {
                    continue;
                }

                var next = i + 1 < count ? ItemContainerGenerator.ContainerFromIndex(i + 1) as TabStripItem : null;
                item.ShowSeparator = next is not null
                    && !item.IsSelected && !next.IsSelected
                    && !item.IsMouseOver && !next.IsMouseOver
                    && !item.IsDragSource && !next.IsDragSource
                    && !(item.IsPinnedTab && next.IsPinnedTab);
            }
        }));
    }

    /// <inheritdoc/>
    protected override DependencyObject GetContainerForItemOverride() => new TabStripItem();

    /// <inheritdoc/>
    protected override bool IsItemItsOwnContainerOverride(object item) => item is TabStripItem;

    /// <inheritdoc/>
    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is TabStripItem container)
        {
            container.Index = Items.IndexOf(item);
        }

        UpdateSeparators();
    }

    /// <inheritdoc/>
    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        base.ClearContainerForItemOverride(element, item);
        if (element is TabStripItem container)
        {
            container.Index = -1;
        }
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size constraint)
    {
        if (!AvailableTabsWidth.Equals(constraint.Width))
        {
            // The panel sits in a scroll viewer (always measured with infinite width): tell it the width changed.
            AvailableTabsWidth = constraint.Width;
            _itemsPanel?.InvalidateMeasure();
        }

        var desired = base.MeasureOverride(constraint);
        return new Size(double.IsFinite(constraint.Width) ? Math.Min(desired.Width, constraint.Width) : desired.Width, desired.Height);
    }

    /// <inheritdoc/>
    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        UpdateSeparators();
        BringSelectedIntoView();
    }

    /// <inheritdoc/>
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);

        // The strip is one tab stop: focus that lands on the list itself goes to the selected tab.
        if (ReferenceEquals(e.NewFocus, this))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => FocusSelectedTab()));
        }
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_dragging && e.Key == Key.Escape)
        {
            EndDrag(commit: false);
            e.Handled = true;
            return;
        }

        var tab = FocusedTab() ?? SelectedItem as TabViewModel;
        var tabs = Shell?.Tabs;
        if (tab is null || tabs is null || e.OriginalSource is TextBox)
        {
            base.OnKeyDown(e);
            return;
        }

        var modifiers = Keyboard.Modifiers;
        var index = tabs.Tabs.IndexOf(tab);
        switch (e.Key)
        {
            case Key.Left when modifiers == ModifierKeys.None:
                SelectAndFocus(index - 1);
                break;
            case Key.Right when modifiers == ModifierKeys.None:
                SelectAndFocus(index + 1);
                break;
            case Key.Home when modifiers == ModifierKeys.None:
                SelectAndFocus(0);
                break;
            case Key.End when modifiers == ModifierKeys.None:
                SelectAndFocus(tabs.Tabs.Count - 1);
                break;
            case Key.Left when modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                tabs.MoveTabLeftCommand.Execute(tab);
                RefocusAfterMove(tab);
                break;
            case Key.Right when modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                tabs.MoveTabRightCommand.Execute(tab);
                RefocusAfterMove(tab);
                break;
            case Key.Enter when modifiers == ModifierKeys.None:
                SelectedItem = tab;
                EditorFocusRequested?.Invoke(this, EventArgs.Empty);
                break;
            case Key.F2 when modifiers == ModifierKeys.None:
                RenameTab(tab);
                break;
            case Key.Delete when modifiers == ModifierKeys.None:
                CloseTab(tab);
                break;
            default:
                base.OnKeyDown(e);
                return;
        }

        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (_scrollViewer is { ScrollableWidth: > 0 } viewer)
        {
            viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        var item = ItemFromSource(e.OriginalSource as DependencyObject);
        _pressed = item is not null && !item.IsWithinCloseButton(e.OriginalSource as DependencyObject)
            && !item.IsWithinRenameBox(e.OriginalSource as DependencyObject) && !item.IsRenamingTab
            ? item
            : null;
        _pressPoint = e.GetPosition(this);
    }

    /// <inheritdoc/>
    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (_pressed is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(position.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance)
            {
                return;
            }

            BeginDrag();
        }

        if (_dragging)
        {
            UpdateDrag(e);
            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        var pressed = _pressed;
        _pressed = null;
        if (_dragging)
        {
            EndDrag(commit: true);
            e.Handled = true;
            return;
        }

        if (pressed is { Tab: { IsRenaming: false } } && e.ClickCount == 1)
        {
            // A click selects the tab; typing continues in the editor.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (pressed.Tab is { IsRenaming: false })
                {
                    EditorFocusRequested?.Invoke(this, EventArgs.Empty);
                }
            }));
        }
    }

    /// <inheritdoc/>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging && !IsMouseCaptured)
        {
            EndDrag(commit: false);
        }
    }

    private static TabStripItem? ItemFromSource(DependencyObject? source)
    {
        for (var node = source; node is not null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is TabStripItem item)
            {
                return item;
            }

            if (node is TabStrip)
            {
                return null;
            }
        }

        return null;
    }

    private TabViewModel? FocusedTab() =>
        ItemFromSource(Keyboard.FocusedElement as DependencyObject)?.Tab;

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Visual indexes changed: renumber the automation IDs (Tab_n, TabClose_n) once the containers follow.
        if (!_renumberQueued)
        {
            _renumberQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                _renumberQueued = false;
                for (var i = 0; i < Items.Count; i++)
                {
                    if (ItemContainerGenerator.ContainerFromIndex(i) is TabStripItem container)
                    {
                        container.Index = i;
                    }
                }
            }));
        }

        UpdateSeparators();
        BringSelectedIntoView();
    }

    private void SelectAndFocus(int index)
    {
        if (index < 0 || index >= Items.Count)
        {
            return;
        }

        SelectedIndex = index;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => FocusSelectedTab()));
    }

    private void RefocusAfterMove(TabViewModel tab) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => ContainerOf(tab)?.Focus()));

    private void BringSelectedIntoView() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => ContainerOf(SelectedItem as TabViewModel)?.BringIntoView()));

    private void OnScrollLeftClick(object sender, RoutedEventArgs e) =>
        _scrollViewer?.ScrollToHorizontalOffset(_scrollViewer.HorizontalOffset - TabStripPanel.MinTabWidth);

    private void OnScrollRightClick(object sender, RoutedEventArgs e) =>
        _scrollViewer?.ScrollToHorizontalOffset(_scrollViewer.HorizontalOffset + TabStripPanel.MinTabWidth);

    // ------------------------------------------------------------------ drag to reorder

    private void BeginDrag()
    {
        if (_pressed is null || _pressed.Tab is null || _itemsPanel is null)
        {
            return;
        }

        _dragOffset = _pressPoint.X - _pressed.TranslatePoint(default, this).X;
        _pressed.IsDragSource = true;
        _pressed.RenderTransform = new TranslateTransform();
        Panel.SetZIndex(_pressed, 1);

        // Capture first: switching the list's own capture raises LostMouseCapture, which must not end this drag.
        CaptureMouse();
        _dragging = true;
        UpdateSeparators();
    }

    private void UpdateDrag(MouseEventArgs e)
    {
        if (_pressed is not { Tab: { } tab } item || _itemsPanel is null || Shell?.Tabs is not { } tabs)
        {
            return;
        }

        var pointer = e.GetPosition(_itemsPanel).X;
        var home = item.TranslatePoint(default, _itemsPanel).X - ((TranslateTransform)item.RenderTransform).X;
        var left = Math.Clamp(pointer - _dragOffset, 0, Math.Max(0, _itemsPanel.ActualWidth - item.ActualWidth));
        ((TranslateTransform)item.RenderTransform).X = left - home;

        // The tab lands where its centre is, within its group (pinned tabs stay in front of the others).
        var centre = left + (item.ActualWidth / 2);
        var oldIndex = tabs.Tabs.IndexOf(tab);
        var target = 0;
        for (var i = 0; i < tabs.Tabs.Count; i++)
        {
            if (i == oldIndex || ItemContainerGenerator.ContainerFromIndex(i) is not TabStripItem other)
            {
                continue;
            }

            var otherLeft = other.TranslatePoint(default, _itemsPanel).X;
            if (centre > otherLeft + (other.ActualWidth / 2))
            {
                target++;
            }
        }

        var pinned = tabs.PinnedCount;
        target = tab.IsPinned ? Math.Clamp(target, 0, Math.Max(0, pinned - 1)) : Math.Clamp(target, pinned, tabs.Tabs.Count - 1);
        _dropIndex = target;
        ShowDropMarker(oldIndex, target);
    }

    private void ShowDropMarker(int oldIndex, int target)
    {
        if (_dropMarker is null || _itemsPanel is null)
        {
            return;
        }

        if (target == oldIndex || ItemContainerGenerator.ContainerFromIndex(target) is not TabStripItem anchor)
        {
            _dropMarker.Visibility = Visibility.Collapsed;
            return;
        }

        var anchorLeft = anchor.TranslatePoint(default, _itemsPanel).X;
        var x = target < oldIndex ? anchorLeft : anchorLeft + anchor.ActualWidth;
        Canvas.SetLeft(_dropMarker, Math.Max(0, x - (_dropMarker.Width / 2)));
        _dropMarker.Visibility = Visibility.Visible;
    }

    private void EndDrag(bool commit)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        var item = _pressed;
        _pressed = null;
        var target = _dropIndex;
        _dropIndex = -1;
        if (_dropMarker is not null)
        {
            _dropMarker.Visibility = Visibility.Collapsed;
        }

        if (item is not null)
        {
            item.IsDragSource = false;
            item.RenderTransform = Transform.Identity;
            Panel.SetZIndex(item, 0);
        }

        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        if (commit && target >= 0 && item?.Tab is { } tab && Shell?.Tabs is { } tabs && tabs.Tabs.IndexOf(tab) != target)
        {
            tabs.Move(tab, target);
            SelectedItem = tab;
        }

        UpdateSeparators();
    }
}
