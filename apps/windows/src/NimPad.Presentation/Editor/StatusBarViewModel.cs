using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NimPad.Core.Domain;
using NimPad.Core.Files;
using NimPad.Core.Services;
using NimPad.Presentation.Infrastructure;
using NimPad.Presentation.Tabs;

namespace NimPad.Presentation.Editor;

/// <summary>The sync slot of the status bar. Phase 1 has no cloud sync, so it never claims "Synced".</summary>
public enum SyncState
{
    /// <summary>No tab is open.</summary>
    None,

    /// <summary>A managed note, stored on this computer only: "Local only".</summary>
    LocalOnly,

    /// <summary>An external file, which NimPad never uploads: "Not synced".</summary>
    NotSynced,

    /// <summary>The note's autosave failed: "Not saved · Retry".</summary>
    SaveFailed,
}

/// <summary>
/// The status bar: caret position and selection, character count, encoding, mode, zoom and the sync slot. The view
/// reports caret moves through <see cref="UpdateCaret"/>, which only formats text when a value changed.
/// </summary>
public sealed partial class StatusBarViewModel : ObservableObject
{
    private readonly ViewModelContext _context;
    private readonly EditorViewModel _editor;
    private int _line = 1;
    private int _column = 1;
    private int _selectionLength;
    private int _textLength = -1;
    private TabViewModel? _trackedTab;

    internal StatusBarViewModel(ViewModelContext context, EditorViewModel editor)
    {
        _context = context;
        _editor = editor;
        editor.PropertyChanged += OnEditorPropertyChanged;
        PositionText = FormatPosition(1, 1, 0);
        Refresh();
    }

    /// <summary>Raised by <see cref="ToggleSidebarCommand"/>; the shell toggles the sidebar.</summary>
    public event EventHandler? ToggleSidebarRequested;

    /// <summary>True when a tab is open (otherwise the right-hand items hide).</summary>
    [ObservableProperty]
    public partial bool HasActiveTab { get; private set; }

    /// <summary><c>Ln 13, Col 31</c>, or <c>Ln 2, Col 24 · 13 selected</c>.</summary>
    [ObservableProperty]
    public partial string PositionText { get; private set; }

    /// <summary><c>312 characters</c>.</summary>
    [ObservableProperty]
    public partial string CharacterCountText { get; private set; } = string.Empty;

    /// <summary><c>UTF-8</c> for notes; the detected encoding for files (<c>UTF-8 with BOM</c>, <c>UTF-16 LE</c>, <c>Windows-1252</c>).</summary>
    [ObservableProperty]
    public partial string EncodingText { get; private set; } = string.Empty;

    /// <summary>The mode's name, for example <c>Plain Text</c> or <c>JSON</c>.</summary>
    [ObservableProperty]
    public partial string ModeText { get; private set; } = string.Empty;

    /// <summary>The zoom, for example <c>100%</c>.</summary>
    [ObservableProperty]
    public partial string ZoomText { get; private set; } = string.Empty;

    /// <summary>The sync slot's state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSaveFailed))]
    public partial SyncState SyncState { get; private set; }

    /// <summary>True when the slot offers Retry.</summary>
    public bool IsSaveFailed => SyncState == SyncState.SaveFailed;

    /// <summary><c>Local only</c>, <c>Not synced</c> or <c>Not saved · Retry</c>.</summary>
    [ObservableProperty]
    public partial string SyncText { get; private set; } = string.Empty;

    /// <summary>The modes the mode picker offers.</summary>
    public IReadOnlyList<FormatInfo> AvailableModes => FormatInfo.All;

    /// <summary>
    /// Reports the caret from the view (1-based line and column, selection length, text length). Cheap: texts are
    /// only rebuilt for values that changed.
    /// </summary>
    public void UpdateCaret(int line, int column, int selectionLength, int textLength)
    {
        if (line != _line || column != _column || selectionLength != _selectionLength)
        {
            _line = line;
            _column = column;
            _selectionLength = selectionLength;
            PositionText = FormatPosition(line, column, selectionLength);
        }

        if (textLength != _textLength)
        {
            _textLength = textLength;
            CharacterCountText = DisplayText.Count(textLength, "character", "characters", _context.Culture);
        }
    }

    /// <summary>Changes the active tab's mode: notes store it, files keep it for the tab.</summary>
    [RelayCommand]
    private Task ChangeModeAsync(object? mode) => _context.GuardAsync(async () =>
    {
        var format = mode switch
        {
            DocumentFormat value => value,
            FormatInfo info => info.Format,
            string text when DocumentFormats.TryParse(text, out var parsed) => parsed,
            _ => (DocumentFormat?)null,
        };
        if (format is not { } target || _editor.ActiveTab is not { } tab || tab.Format == target)
        {
            return;
        }

        if (tab.IsManaged && tab.NodeId is { } nodeId)
        {
            var node = await _context.Core.Documents.SetFormatAsync(nodeId, target);
            _context.Model.Upsert(node, NodeChangeKind.FormatChanged);
        }

        tab.Format = target;
    });

    /// <summary>Back to 100 % zoom.</summary>
    [RelayCommand]
    private void ResetZoom() => _editor.ResetZoom();

    /// <summary>Shows or hides the notes sidebar.</summary>
    [RelayCommand]
    private void ToggleSidebar() => ToggleSidebarRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Saves the active note now after a failed autosave.</summary>
    [RelayCommand]
    private async Task RetrySaveAsync()
    {
        if (_editor.ActiveTab is { IsManaged: true } tab)
        {
            try
            {
                await tab.RetrySaveAsync();
            }
            catch (Exception)
            {
                // The tab shows the failure (banner and SaveState).
            }
        }
    }

    private static string FormatPosition(int line, int column, int selectionLength)
    {
        var position = string.Create(CultureInfo.InvariantCulture, $"Ln {line}, Col {column}");
        return selectionLength > 0 ? string.Create(CultureInfo.InvariantCulture, $"{position} · {selectionLength} selected") : position;
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(EditorViewModel.ActiveTab):
                if (_editor.ActiveTab is { } tab)
                {
                    // The view reports the real caret once the document is shown.
                    UpdateCaret(1, 1, 0, tab.TextLength);
                }

                Refresh();
                break;
            case nameof(EditorViewModel.Format):
            case nameof(EditorViewModel.Zoom):
                Refresh();
                break;
        }
    }

    private void Refresh()
    {
        var tab = _editor.ActiveTab;
        if (_trackedTab != tab)
        {
            if (_trackedTab is not null)
            {
                _trackedTab.PropertyChanged -= OnTabPropertyChanged;
            }

            _trackedTab = tab;
            if (tab is not null)
            {
                tab.PropertyChanged += OnTabPropertyChanged;
            }
        }

        HasActiveTab = tab is not null;
        ModeText = tab is null ? string.Empty : FormatInfo.DisplayNameOf(_editor.Format);
        ZoomText = string.Create(CultureInfo.InvariantCulture, $"{_editor.Zoom}%");
        EncodingText = tab switch
        {
            null => string.Empty,
            { IsExternal: true, Encoding: { } encoding } => encoding.DisplayName,
            { IsExternal: true } => string.Empty,
            _ => TextEncodingInfo.Utf8.DisplayName,
        };
        (SyncState, SyncText) = tab switch
        {
            null => (SyncState.None, string.Empty),
            { IsExternal: true } => (SyncState.NotSynced, "Not synced"),
            { SaveState: TabSaveState.Failed } => (SyncState.SaveFailed, "Not saved · Retry"),
            _ => (SyncState.LocalOnly, "Local only"),
        };
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TabViewModel.SaveState):
            case nameof(TabViewModel.Encoding):
                Refresh();
                break;
            case nameof(TabViewModel.TextLength) when sender is TabViewModel tab:
                UpdateCaret(_line, _column, _selectionLength, tab.TextLength);
                break;
        }
    }
}
