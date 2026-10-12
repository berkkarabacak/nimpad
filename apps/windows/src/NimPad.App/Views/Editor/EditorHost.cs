using System.ComponentModel;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using NimPad.App.Highlighting;
using NimPad.App.Services;
using NimPad.Presentation.Editor;
using NimPad.Presentation.Shell;
using NimPad.Presentation.Tabs;

namespace NimPad.App.Views.Editor;

/// <summary>
/// Connects one AvalonEdit <see cref="TextEditor"/> to the tabs (the document lifecycle of
/// <c>NimPad.Presentation/README.md</c>): one <see cref="TextDocument"/> per tab, created when the tab first becomes
/// active (from <see cref="TabViewModel.LoadAsync"/>), wrapped in an <see cref="EditorBuffer"/> and attached with
/// <see cref="TabViewModel.AttachBuffer"/>; every document change calls <see cref="TabViewModel.OnBufferChanged"/>;
/// switching tabs swaps <see cref="TextEditor.Document"/>, records the old tab's caret, selection and scroll
/// (<see cref="TabViewModel.UpdateViewState"/>) and restores the new tab's at <see cref="DispatcherPriority.Loaded"/>;
/// <see cref="TabViewModel.ReplaceTextRequested"/> replaces the text in place; a closed tab's document is released.
/// While a tab's text is loading (and during shutdown) the editor is read-only. Caret moves go to the status bar.
/// </summary>
public sealed class EditorHost : IDisposable
{
    private readonly TextEditor _editor;
    private readonly ShellViewModel _shell;
    private readonly Func<bool> _isDarkTheme;
    private readonly Dictionary<TabViewModel, Entry> _entries = [];
    private readonly HashSet<TabViewModel> _loading = [];
    private readonly TextDocument _placeholder = new();
    private TabViewModel? _active;
    private bool _restoring;
    private int _showGeneration;
    private bool _disposed;

    /// <summary>Starts managing <paramref name="editor"/> for <paramref name="shell"/>'s tabs.</summary>
    /// <param name="editor">The editor.</param>
    /// <param name="shell">The window's view model.</param>
    /// <param name="isDarkTheme">Whether the dark theme is applied (for the syntax colors).</param>
    public EditorHost(TextEditor editor, ShellViewModel shell, Func<bool> isDarkTheme)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(isDarkTheme);
        _editor = editor;
        _shell = shell;
        _isDarkTheme = isDarkTheme;
        _placeholder.UndoStack.SizeLimit = 0;
        _editor.Document = _placeholder;
        _editor.IsReadOnly = true;
        shell.Editor.PropertyChanged += OnEditorPropertyChanged;
        shell.Tabs.TabClosed += OnTabClosed;
        shell.PropertyChanged += OnShellPropertyChanged;
        editor.TextArea.Caret.PositionChanged += OnCaretOrSelectionChanged;
        editor.TextArea.SelectionChanged += OnCaretOrSelectionChanged;
        editor.TextArea.TextView.ScrollOffsetChanged += OnScrollOffsetChanged;
        Show(shell.Editor.ActiveTab);
    }

    /// <summary>Raised after a tab's document is shown and its view state restored.</summary>
    public event EventHandler? DocumentShown;

    /// <summary>The tab the editor belongs to now.</summary>
    public TabViewModel? ActiveTab => _active;

    /// <summary>True when the editor shows the active tab's own document (not the empty placeholder of a load).</summary>
    public bool IsShowingActiveDocument =>
        _active is not null && _entries.TryGetValue(_active, out var entry) && ReferenceEquals(_editor.Document, entry.Document);

    /// <summary>The document of <paramref name="tab"/>, once its text is loaded.</summary>
    public TextDocument? DocumentOf(TabViewModel tab) => _entries.TryGetValue(tab, out var entry) ? entry.Document : null;

    /// <summary>Re-applies the syntax colors (after a theme change).</summary>
    public void RefreshHighlighting()
    {
        if (!IsShowingActiveDocument)
        {
            _editor.SyntaxHighlighting = null;
            return;
        }

        try
        {
            _editor.SyntaxHighlighting = LargeDocumentPolicy.Check(_editor.Document) == LargeDocumentReason.None
                ? HighlightingProvider.Default.Get(_shell.Editor.Format, _isDarkTheme())
                : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Plain text is always a safe fallback.
            FileLog.Warn("The syntax highlighting could not be applied.", ex);
            _editor.SyntaxHighlighting = null;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shell.Editor.PropertyChanged -= OnEditorPropertyChanged;
        _shell.Tabs.TabClosed -= OnTabClosed;
        _shell.PropertyChanged -= OnShellPropertyChanged;
        _editor.TextArea.Caret.PositionChanged -= OnCaretOrSelectionChanged;
        _editor.TextArea.SelectionChanged -= OnCaretOrSelectionChanged;
        _editor.TextArea.TextView.ScrollOffsetChanged -= OnScrollOffsetChanged;
        if (_active is not null)
        {
            _active.PropertyChanged -= OnActiveTabPropertyChanged;
        }

        foreach (var entry in _entries.Values.ToList())
        {
            Release(entry);
        }
    }

    private void Show(TabViewModel? tab)
    {
        if (_disposed)
        {
            return;
        }

        if (!ReferenceEquals(_active, tab))
        {
            if (_active is { } previous)
            {
                CaptureViewState(previous);
                previous.PropertyChanged -= OnActiveTabPropertyChanged;
            }

            _active = tab;
            if (tab is not null)
            {
                tab.PropertyChanged += OnActiveTabPropertyChanged;
            }
        }

        if (tab is null)
        {
            ShowPlaceholder();
            return;
        }

        if (_entries.TryGetValue(tab, out var entry))
        {
            ShowEntry(tab, entry);
        }
        else
        {
            ShowPlaceholder();
            _ = EnsureDocumentAsync(tab);
        }
    }

    private void ShowPlaceholder()
    {
        _showGeneration++;
        _restoring = false;
        if (!ReferenceEquals(_editor.Document, _placeholder))
        {
            _editor.Document = _placeholder;
        }

        _editor.IsReadOnly = true;
        _editor.SyntaxHighlighting = null;
    }

    private void ShowEntry(TabViewModel tab, Entry entry)
    {
        var generation = ++_showGeneration;
        _restoring = true;
        if (!ReferenceEquals(_editor.Document, entry.Document))
        {
            _editor.Document = entry.Document;
        }

        UpdateReadOnly();
        RefreshHighlighting();
        _editor.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (generation != _showGeneration || !ReferenceEquals(_active, tab) || _disposed)
            {
                return;
            }

            try
            {
                RestoreViewState(tab, entry.Document);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                FileLog.Warn("The caret and scroll position of a tab could not be restored.", ex);
            }
            finally
            {
                _restoring = false;
            }

            ReportCaret();
            DocumentShown?.Invoke(this, EventArgs.Empty);
        }));
    }

    private async Task EnsureDocumentAsync(TabViewModel tab)
    {
        if (_entries.ContainsKey(tab) || !_loading.Add(tab))
        {
            return;
        }

        try
        {
            string text;
            try
            {
                text = await tab.LoadAsync();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // The tab shows LoadState.Failed with its message and Retry.
                FileLog.Warn($"A tab's text could not be loaded ({ex.GetType().Name}).", ex);
                return;
            }

            if (_disposed || _entries.ContainsKey(tab) || !_shell.Tabs.Tabs.Contains(tab))
            {
                return;
            }

            var document = new TextDocument(text);
            var entry = new Entry(tab, document, new EditorBuffer(document));
            entry.Changed = (_, _) =>
            {
                entry.Buffer.OnDocumentChanged();
                tab.OnBufferChanged();
            };
            entry.ReplaceRequested = (_, e) => document.Replace(0, document.TextLength, e.Text);
            document.Changed += entry.Changed;
            tab.ReplaceTextRequested += entry.ReplaceRequested;
            _entries[tab] = entry;
            tab.AttachBuffer(entry.Buffer);
            if (ReferenceEquals(tab, _active))
            {
                ShowEntry(tab, entry);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FileLog.Error("A tab's document could not be attached.", ex);
        }
        finally
        {
            _loading.Remove(tab);
        }
    }

    private void RestoreViewState(TabViewModel tab, TextDocument document)
    {
        var length = document.TextLength;
        var caret = Math.Clamp(tab.CaretOffset, 0, length);
        var selectionStart = Math.Clamp(tab.SelectionStart, 0, length);
        var selectionLength = Math.Clamp(tab.SelectionLength, 0, length - selectionStart);
        if (selectionLength > 0)
        {
            _editor.Select(selectionStart, selectionLength);
        }
        else
        {
            _editor.TextArea.ClearSelection();
        }

        _editor.TextArea.Caret.Offset = caret;
        _editor.ScrollToVerticalOffset(tab.VerticalOffset);
        _editor.ScrollToHorizontalOffset(tab.HorizontalOffset);
    }

    private void CaptureViewState(TabViewModel tab)
    {
        if (_restoring || !_entries.TryGetValue(tab, out var entry) || !ReferenceEquals(_editor.Document, entry.Document))
        {
            return;
        }

        var area = _editor.TextArea;
        var selection = area.Selection;
        var (start, length) = selection.IsEmpty
            ? (area.Caret.Offset, 0)
            : (selection.SurroundingSegment.Offset, selection.SurroundingSegment.Length);
        tab.UpdateViewState(area.Caret.Offset, start, length, _editor.VerticalOffset, _editor.HorizontalOffset);
    }

    private void ReportCaret()
    {
        if (!IsShowingActiveDocument)
        {
            return;
        }

        var area = _editor.TextArea;
        var selected = area.Selection.IsEmpty ? 0 : area.Selection.Length;
        _shell.StatusBar.UpdateCaret(area.Caret.Line, area.Caret.Column, selected, _editor.Document.TextLength);
    }

    private void UpdateReadOnly() => _editor.IsReadOnly = _shell.IsShuttingDown || !IsShowingActiveDocument;

    private void Release(Entry entry)
    {
        entry.Document.Changed -= entry.Changed;
        entry.Tab.ReplaceTextRequested -= entry.ReplaceRequested;
        _entries.Remove(entry.Tab);
    }

    private void OnCaretOrSelectionChanged(object? sender, EventArgs e)
    {
        if (_restoring || _active is not { } tab || !IsShowingActiveDocument)
        {
            return;
        }

        CaptureViewState(tab);
        ReportCaret();
    }

    private void OnScrollOffsetChanged(object? sender, EventArgs e)
    {
        if (!_restoring && _active is { } tab && IsShowingActiveDocument)
        {
            CaptureViewState(tab);
        }
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(EditorViewModel.ActiveTab):
                Show(_shell.Editor.ActiveTab);
                break;
            case nameof(EditorViewModel.Format):
                RefreshHighlighting();
                break;
        }
    }

    private void OnActiveTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.LoadState) && sender is TabViewModel { LoadState: TabLoadState.Loaded } tab
            && ReferenceEquals(tab, _active) && !_entries.ContainsKey(tab))
        {
            // Loaded by someone else (the startup load, Retry): show it.
            _ = EnsureDocumentAsync(tab);
        }
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.IsShuttingDown))
        {
            if (_active is { } tab)
            {
                // The session saved at shutdown gets the latest caret.
                CaptureViewState(tab);
            }

            UpdateReadOnly();
        }
    }

    private void OnTabClosed(object? sender, TabViewModel tab)
    {
        if (!_entries.TryGetValue(tab, out var entry))
        {
            return;
        }

        if (ReferenceEquals(_editor.Document, entry.Document))
        {
            ShowPlaceholder();
        }

        Release(entry);
    }

    private sealed class Entry(TabViewModel tab, TextDocument document, EditorBuffer buffer)
    {
        public TabViewModel Tab { get; } = tab;

        public TextDocument Document { get; } = document;

        public EditorBuffer Buffer { get; } = buffer;

        public EventHandler<DocumentChangeEventArgs>? Changed { get; set; }

        public EventHandler<ReplaceTextRequestedEventArgs>? ReplaceRequested { get; set; }
    }
}
