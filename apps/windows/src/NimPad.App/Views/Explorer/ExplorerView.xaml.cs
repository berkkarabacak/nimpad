using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using NimPad.Presentation.Explorer;

namespace NimPad.App.Views.Explorer;

/// <summary>
/// STUB of the notes sidebar (it is replaced by the full explorer view): search field, new note / new folder, one
/// list of rows (Enter or double-click opens a note or toggles a folder, Left/Right collapse/expand, Delete moves to
/// the Trash) and the Trash entry. Rows carry the contract automation IDs (<c>ExplorerRow_&lt;id&gt;</c>,
/// <c>ExplorerSearchResult_&lt;id&gt;</c>) and their names. DataContext: the <see cref="ExplorerViewModel"/>.
/// </summary>
public partial class ExplorerView : UserControl
{
    private ExplorerViewModel? _explorer;

    /// <summary>Creates the view.</summary>
    public ExplorerView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => EnsureRowStyle();
    }

    /// <summary>Moves keyboard focus to the search field and selects its text (Ctrl+B, F6).</summary>
    /// <returns><see langword="true"/> when the field took focus.</returns>
    public bool FocusSearch()
    {
        if (!Search.IsVisible)
        {
            return false;
        }

        Search.ApplyTemplate();
        Search.FocusAndSelectAll();
        return Search.IsKeyboardFocusWithin;
    }

    // The rows use the design system's row style (NoteRowItem); it is looked up once the view is in a tree with the
    // resources, and the contract's automation ID and name are added to it.
    private void EnsureRowStyle()
    {
        if (List.ItemContainerStyle is not null)
        {
            return;
        }

        var style = new Style(typeof(ListBoxItem), TryFindResource("NoteRowItem") as Style);
        style.Setters.Add(new Setter(AutomationProperties.AutomationIdProperty, new Binding { Converter = ExplorerConverters.RowAutomationId }));
        // Node rows have a Name, the others a Title; both follow renames live.
        var name = new PriorityBinding();
        name.Bindings.Add(new Binding(nameof(ExplorerNodeRowViewModel.Name)));
        name.Bindings.Add(new Binding(nameof(ExplorerHeaderRowViewModel.Title)));
        style.Setters.Add(new Setter(AutomationProperties.NameProperty, name));
        style.Setters.Add(new Setter(FocusableProperty, new Binding { Converter = ExplorerConverters.IsFocusableRow }));
        List.ItemContainerStyle = style;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_explorer is not null)
        {
            _explorer.ScrollIntoViewRequested -= OnScrollIntoViewRequested;
        }

        _explorer = e.NewValue as ExplorerViewModel;
        if (_explorer is not null)
        {
            _explorer.ScrollIntoViewRequested += OnScrollIntoViewRequested;
        }
    }

    // Esc in an empty search field returns to the editor (the search box itself clears a filled field first).
    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None && string.IsNullOrEmpty(Search.Text) && _explorer is { } explorer)
        {
            Execute(explorer.ClearSearchCommand, null);
            e.Handled = true;
        }
    }

    private void OnScrollIntoViewRequested(object? sender, ScrollIntoViewRequestedEventArgs e) => List.ScrollIntoView(e.Row);

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && ItemsControl.ContainerFromElement(List, e.OriginalSource as DependencyObject) is ListBoxItem { DataContext: ExplorerRowViewModel row })
        {
            Execute(_explorer?.OpenCommand, row);
            e.Handled = true;
        }
    }

    private void OnListPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_explorer is not { } explorer || explorer.SelectedRow is not { } row || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                Execute(explorer.OpenCommand, row);
                break;
            case Key.Left when row is ExplorerNodeRowViewModel { IsFolder: true, IsExpanded: true }:
                Execute(explorer.CollapseRowCommand, row);
                break;
            case Key.Right when row is ExplorerNodeRowViewModel { IsFolder: true, IsExpanded: false }:
                Execute(explorer.ExpandRowCommand, row);
                break;
            case Key.Delete:
                Execute(explorer.DeleteCommand, row);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private static void Execute(ICommand? command, object? parameter)
    {
        if (command is not null && command.CanExecute(parameter))
        {
            command.Execute(parameter);
        }
    }
}

/// <summary>Converters of the sidebar stub.</summary>
public static class ExplorerConverters
{
    /// <summary>A tree depth → the row's left indent (16 px per level).</summary>
    public static IValueConverter DepthToIndent { get; } = new DepthToIndentConverter();

    /// <summary>A row → its automation ID (<c>ExplorerRow_&lt;id&gt;</c>, <c>ExplorerSearchResult_&lt;id&gt;</c>).</summary>
    public static IValueConverter RowAutomationId { get; } = new RowAutomationIdConverter();

    /// <summary>A row → whether its container takes keyboard focus (section headers do not).</summary>
    public static IValueConverter IsFocusableRow { get; } = new IsFocusableRowConverter();

    /// <summary>The automation ID of <paramref name="row"/>.</summary>
    public static string AutomationIdOf(object? row) => row switch
    {
        ExplorerSearchResultRowViewModel result => "ExplorerSearchResult_" + result.Id,
        ExplorerNodeRowViewModel node => "ExplorerRow_" + node.Id,
        ExplorerTrashRowViewModel trash => "ExplorerRow_" + trash.Id,
        ExplorerHeaderRowViewModel header => "ExplorerHeader_" + header.Section,
        _ => string.Empty,
    };

    private sealed class DepthToIndentConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            new Thickness(value is int depth ? Math.Max(0, depth) * 16 : 0, 0, 0, 0);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
    }

    private sealed class RowAutomationIdConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => AutomationIdOf(value);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
    }

    private sealed class IsFocusableRowConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not ExplorerHeaderRowViewModel;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
    }
}
