using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NimPad.Core.Domain;
using NimPad.Core.Ids;
using NimPad.Presentation.Infrastructure;
using NimPad.Presentation.Settings;
using NimPad.Presentation.Tabs;
using NimPad.Presentation.Workspace;

namespace NimPad.Presentation.Editor;

/// <summary>One segment of the breadcrumb line above the editor.</summary>
/// <param name="NodeId">The folder or note.</param>
/// <param name="Name">Its name.</param>
/// <param name="IsLast">True for the note itself (the last segment).</param>
public sealed record BreadcrumbViewModel(NodeId NodeId, string Name, bool IsLast);

/// <summary>A request to show a node in the explorer (breadcrumb click).</summary>
public sealed class RevealRequestedEventArgs(NodeId nodeId) : EventArgs
{
    /// <summary>The node to reveal.</summary>
    public NodeId NodeId { get; } = nodeId;
}

/// <summary>
/// The state of the editor for the active tab: breadcrumbs (or the file path), the syntax mode, the settings-driven
/// look (font, size × zoom, wrap, line numbers, tab size), zoom, and the empty-note hint.
/// </summary>
public sealed partial class EditorViewModel : ObservableObject
{
    /// <summary>The zoom step of Ctrl+Plus / Ctrl+Minus, in percent.</summary>
    public const int ZoomStep = 10;

    /// <summary>The smallest zoom, in percent.</summary>
    public const int MinZoom = 50;

    /// <summary>The largest zoom, in percent.</summary>
    public const int MaxZoom = 400;

    /// <summary>The hint shown in an empty note that was never typed in (never saved into the note).</summary>
    public const string EmptyHint = "Start typing. It saves on its own.";

    private readonly ViewModelContext _context;
    private readonly TabsViewModel _tabs;

    internal EditorViewModel(ViewModelContext context, TabsViewModel tabs)
    {
        _context = context;
        _tabs = tabs;
        tabs.PropertyChanged += OnTabsPropertyChanged;
        context.Settings.Changed += (_, _) => ApplySettings();
        context.Model.Changed += OnModelChanged;
        ApplySettings();
    }

    /// <summary>The active tab, or <see langword="null"/> when no tab is open.</summary>
    [ObservableProperty]
    public partial TabViewModel? ActiveTab { get; private set; }

    /// <summary>The note's folders and the note itself; empty for external files.</summary>
    public ObservableCollection<BreadcrumbViewModel> Breadcrumbs { get; } = [];

    /// <summary>The full path of an external file (shown instead of breadcrumbs), or <see langword="null"/>.</summary>
    [ObservableProperty]
    public partial string? FilePathText { get; private set; }

    /// <summary>The active tab's syntax mode.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormatDisplayName))]
    public partial DocumentFormat Format { get; private set; }

    /// <summary>The mode's name, for example <c>Plain Text</c>.</summary>
    public string FormatDisplayName => FormatInfo.DisplayNameOf(Format);

    /// <summary>The editor font family (setting).</summary>
    [ObservableProperty]
    public partial string FontFamily { get; private set; } = AppSettings.DefaultEditorFontFamily;

    /// <summary>The effective font size in pixels: the font size setting × <see cref="Zoom"/> / 100.</summary>
    [ObservableProperty]
    public partial double FontSize { get; private set; }

    /// <summary>Whether long lines wrap (setting).</summary>
    [ObservableProperty]
    public partial bool WordWrap { get; private set; }

    /// <summary>Whether line numbers show (setting).</summary>
    [ObservableProperty]
    public partial bool ShowLineNumbers { get; private set; }

    /// <summary>The tab size (setting).</summary>
    [ObservableProperty]
    public partial int TabSize { get; private set; }

    /// <summary>The zoom in percent (50–400, step 10).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ZoomInCommand), nameof(ZoomOutCommand), nameof(ResetZoomCommand))]
    public partial int Zoom { get; private set; } = AppSettings.DefaultZoom;

    /// <summary>The editor's left and right padding (32 in the wide layout, 24 otherwise; set by the shell).</summary>
    [ObservableProperty]
    public partial double HorizontalPadding { get; internal set; } = 32;

    /// <summary>True when the active note is empty and was never typed in: show <see cref="EmptyHint"/>.</summary>
    [ObservableProperty]
    public partial bool ShowEmptyHint { get; private set; }

    /// <summary>Raised when a breadcrumb asks to show its node in the explorer.</summary>
    public event EventHandler<RevealRequestedEventArgs>? RevealRequested;

    /// <summary>Zooms in by <see cref="ZoomStep"/> (Ctrl+Plus).</summary>
    [RelayCommand(CanExecute = nameof(CanZoomIn))]
    public void ZoomIn() => SetZoom(Zoom + ZoomStep);

    /// <summary>Zooms out by <see cref="ZoomStep"/> (Ctrl+Minus).</summary>
    [RelayCommand(CanExecute = nameof(CanZoomOut))]
    public void ZoomOut() => SetZoom(Zoom - ZoomStep);

    /// <summary>Back to 100 % (Ctrl+0).</summary>
    [RelayCommand(CanExecute = nameof(CanResetZoom))]
    public void ResetZoom() => SetZoom(AppSettings.DefaultZoom);

    /// <summary>Sets the zoom (clamped to 50–400 and rounded to the step) and remembers it.</summary>
    public void SetZoom(int percent)
    {
        var rounded = (int)Math.Round(percent / (double)ZoomStep, MidpointRounding.AwayFromZero) * ZoomStep;
        var clamped = Math.Clamp(rounded, MinZoom, MaxZoom);
        _context.Settings.Update(settings => settings with { Zoom = clamped });
    }

    /// <summary>Shows a breadcrumb's folder (or the note) in the explorer.</summary>
    [RelayCommand]
    private void NavigateToBreadcrumb(BreadcrumbViewModel? crumb)
    {
        if (crumb is not null)
        {
            RevealRequested?.Invoke(this, new RevealRequestedEventArgs(crumb.NodeId));
        }
    }

    private bool CanZoomIn() => Zoom < MaxZoom;

    private bool CanZoomOut() => Zoom > MinZoom;

    private bool CanResetZoom() => Zoom != AppSettings.DefaultZoom;

    private void ApplySettings()
    {
        var settings = _context.Settings.Current;
        FontFamily = settings.EditorFontFamily;
        WordWrap = settings.WordWrap;
        ShowLineNumbers = settings.ShowLineNumbers;
        TabSize = settings.TabSize;
        Zoom = Math.Clamp(settings.Zoom, MinZoom, MaxZoom);
        FontSize = settings.EditorFontSize * Zoom / 100.0;
    }

    private void OnTabsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabsViewModel.SelectedTab))
        {
            ActiveTab = _tabs.SelectedTab;
        }
    }

    partial void OnActiveTabChanged(TabViewModel? oldValue, TabViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnActiveTabPropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnActiveTabPropertyChanged;
        }

        Refresh();
    }

    private void OnActiveTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TabViewModel.Format):
            case nameof(TabViewModel.FilePath):
            case nameof(TabViewModel.Title):
                Refresh();
                break;
            case nameof(TabViewModel.TextLength):
            case nameof(TabViewModel.HasBeenEdited):
            case nameof(TabViewModel.LoadState):
                UpdateEmptyHint();
                break;
        }
    }

    private void OnModelChanged(object? sender, WorkspaceModelChangedEventArgs e)
    {
        if (ActiveTab is { IsManaged: true })
        {
            UpdateBreadcrumbs();
        }
    }

    private void Refresh()
    {
        var tab = ActiveTab;
        Format = tab?.Format ?? DocumentFormat.Plain;
        FilePathText = tab is { IsExternal: true } ? tab.FilePath : null;
        UpdateBreadcrumbs();
        UpdateEmptyHint();
    }

    private void UpdateBreadcrumbs()
    {
        var path = ActiveTab is { IsManaged: true, NodeId: { } nodeId } ? _context.Model.GetPath(nodeId) : [];
        var crumbs = path.Select((node, index) => new BreadcrumbViewModel(node.Id, node.Name, index == path.Count - 1)).ToList();
        if (crumbs.SequenceEqual(Breadcrumbs))
        {
            return;
        }

        Breadcrumbs.Clear();
        foreach (var crumb in crumbs)
        {
            Breadcrumbs.Add(crumb);
        }
    }

    private void UpdateEmptyHint() =>
        ShowEmptyHint = ActiveTab is { IsManaged: true, LoadState: TabLoadState.Loaded, TextLength: 0, HasBeenEdited: false };
}
