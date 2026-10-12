using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NimPad.Presentation.Infrastructure;
using NimPad.Presentation.Platform;
using NimPad.Presentation.Tabs;

namespace NimPad.Presentation.Find;

/// <summary>A list of edits the view applies to the active document as one undo step.</summary>
public sealed class ApplyEditsRequestedEventArgs(IReadOnlyList<TextEdit> edits) : EventArgs
{
    /// <summary>The edits in text order, with offsets in the current text (apply them from last to first).</summary>
    public IReadOnlyList<TextEdit> Edits { get; } = edits;
}

/// <summary>A request to select (and scroll to) a match.</summary>
public sealed class SelectMatchRequestedEventArgs(TextMatch match) : EventArgs
{
    /// <summary>The match to select.</summary>
    public TextMatch Match { get; } = match;
}

/// <summary>
/// The floating find and replace panel (design page 9): query, replacement, Aa / whole word / regex, "1 of 2",
/// previous / next, Replace and Replace all. Searches run over a snapshot of the active tab's text, again when the
/// query or an option changes and 150 ms after the document changes.
/// </summary>
/// <remarks>
/// The view handles <see cref="SelectMatchRequested"/> (select and scroll), <see cref="ApplyEditsRequested"/> (apply
/// as one undo group; the panel searches again right after) and <see cref="Closed"/> (focus returns to the editor
/// with the match selected). The shell reports document changes; the panel also searches again when the active tab's
/// document is attached (a tab that loads after it became active).
/// </remarks>
public sealed partial class FindReplaceViewModel : ObservableObject
{
    /// <summary>The quiet period after a document change before the matches are recomputed.</summary>
    public static readonly TimeSpan DocumentChangeDelay = TimeSpan.FromMilliseconds(150);

    // Texts up to this length are searched synchronously on the UI thread; longer ones on the thread pool.
    private const int SynchronousSearchLimit = 1_000_000;

    private readonly ViewModelContext _context;
    private readonly Debouncer _documentChanged;
    private TabViewModel? _tab;
    private string? _snapshot;
    private long _snapshotVersion = -1;
    private IEditorBuffer? _snapshotBuffer;
    private long _searchGeneration;
    private CancellationTokenSource? _searchCancellation;
    private bool _suppressSearch;
    private bool _truncated;
    private int? _pendingAnchor;

    internal FindReplaceViewModel(ViewModelContext context)
    {
        _context = context;
        _documentChanged = new Debouncer(context.Clock, context.Dispatcher, DocumentChangeDelay, () => Search(SelectionMode.KeepPosition, null));
    }

    /// <summary>True while the panel is shown.</summary>
    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    /// <summary>True when the replace row is shown.</summary>
    [ObservableProperty]
    public partial bool IsReplaceMode { get; set; }

    /// <summary>The text or pattern to find.</summary>
    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    /// <summary>The replacement (regex mode: <c>$1</c>, <c>${name}</c> and <c>$$</c> substitute).</summary>
    [ObservableProperty]
    public partial string Replacement { get; set; } = string.Empty;

    /// <summary>Case-sensitive search (Aa, Alt+C).</summary>
    [ObservableProperty]
    public partial bool MatchCase { get; set; }

    /// <summary>Whole words only (Alt+W).</summary>
    [ObservableProperty]
    public partial bool WholeWord { get; set; }

    /// <summary>Regular expression (Alt+R).</summary>
    [ObservableProperty]
    public partial bool UseRegex { get; set; }

    /// <summary>The matches in the active text, in text order (highlighted by the view).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMatches), nameof(MatchCount))]
    [NotifyCanExecuteChangedFor(nameof(FindNextCommand), nameof(FindPreviousCommand), nameof(ReplaceCommand), nameof(ReplaceAllCommand))]
    public partial IReadOnlyList<TextMatch> Matches { get; private set; } = [];

    /// <summary>How many matches there are.</summary>
    public int MatchCount => Matches.Count;

    /// <summary>True when there is at least one match (previous and next are enabled).</summary>
    public bool HasMatches => Matches.Count > 0;

    /// <summary>The index of the current match in <see cref="Matches"/>, or -1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentMatch))]
    public partial int CurrentIndex { get; private set; } = -1;

    /// <summary>The current match (drawn with the "current match" color), or <see langword="null"/>.</summary>
    public TextMatch? CurrentMatch => CurrentIndex >= 0 && CurrentIndex < Matches.Count ? Matches[CurrentIndex] : null;

    /// <summary><c>1 of 2</c>, <c>No results</c>, or the pattern error such as <c>Unmatched “(”</c>; empty for an empty query.</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    /// <summary>True when <see cref="StatusText"/> is a pattern error.</summary>
    [ObservableProperty]
    public partial bool HasError { get; private set; }

    /// <summary>True while a long text is being searched in the background.</summary>
    [ObservableProperty]
    public partial bool IsSearching { get; private set; }

    /// <summary>Raised to select and reveal a match.</summary>
    public event EventHandler<SelectMatchRequestedEventArgs>? SelectMatchRequested;

    /// <summary>Raised by the Replace and Replace all commands with the edits to apply as one undo step.</summary>
    public event EventHandler<ApplyEditsRequestedEventArgs>? ApplyEditsRequested;

    /// <summary>Raised when the panel opens or switches mode; the view focuses the find (or replace) field.</summary>
    public event EventHandler? FocusRequested;

    /// <summary>Raised when the panel closes; focus returns to the editor.</summary>
    public event EventHandler? Closed;

    private enum SelectionMode
    {
        // Pick the first match at or after the anchor, and select it in the editor.
        SelectNearest,

        // Keep the current match's position (the document changed); do not move the editor's selection.
        KeepPosition,
    }

    /// <summary>Opens Find (Ctrl+F); a single-line selection becomes the query.</summary>
    /// <param name="selectedText">The editor's selection, or <see langword="null"/>.</param>
    public void Open(string? selectedText = null) => OpenCore(selectedText, replace: false);

    /// <summary>Opens Replace (Ctrl+H); a single-line selection becomes the query.</summary>
    /// <param name="selectedText">The editor's selection, or <see langword="null"/>.</param>
    public void OpenReplace(string? selectedText = null) => OpenCore(selectedText, replace: true);

    /// <summary>Closes the panel (Esc); highlights disappear and focus returns to the editor.</summary>
    [RelayCommand]
    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        _documentChanged.Cancel();
        _searchCancellation?.Cancel();
        _snapshot = null;
        _snapshotVersion = -1;
        Matches = [];
        CurrentIndex = -1;
        UpdateStatus(null);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects the next match, wrapping to the first (Enter).</summary>
    [RelayCommand(CanExecute = nameof(HasMatches))]
    public void FindNext() => Step(1);

    /// <summary>Selects the previous match, wrapping to the last (Shift+Enter).</summary>
    [RelayCommand(CanExecute = nameof(HasMatches))]
    public void FindPrevious() => Step(-1);

    /// <summary>
    /// The edit that replaces the current match (and moves on to the next match once applied). When the matches are
    /// out of date, or there is no current match, it selects the next match instead and returns <see langword="null"/>.
    /// </summary>
    public TextEdit? Replace()
    {
        if (!EnsureFresh() || CurrentMatch is not { } match || _snapshot is not { } text)
        {
            if (HasMatches && CurrentIndex < 0)
            {
                Step(1);
            }

            return null;
        }

        TextEdit edit;
        try
        {
            edit = TextSearchEngine.Replace(text, match, Replacement, Options);
        }
        catch (RegexMatchTimeoutException)
        {
            UpdateStatus("The pattern takes too long to search. Try a simpler one.");
            return null;
        }
        catch (ArgumentException ex)
        {
            // An invalid substitution in the replacement.
            UpdateStatus(ex.Message);
            return null;
        }

        _pendingAnchor = edit.Offset + edit.Text.Length;
        return edit;
    }

    /// <summary>
    /// The edits that replace every match of the active text, with no limit on their number (apply them as one undo
    /// step). Runs on the calling thread; <see cref="ReplaceAllCommand"/> moves long texts off the UI thread. An
    /// invalid pattern or replacement returns nothing and shows the reason in <see cref="StatusText"/>.
    /// </summary>
    public IReadOnlyList<TextEdit> ReplaceAll()
    {
        if (!IsOpen || Query.Length == 0 || _tab?.Buffer is not { } buffer)
        {
            return [];
        }

        var result = TextSearchEngine.ReplaceEveryMatch(SnapshotOf(buffer), Options, Replacement);
        if (result.Error is { } error)
        {
            UpdateStatus(error);
            return [];
        }

        return result.Edits;
    }

    /// <summary>Replaces the current match and selects the next one (Enter in the replace field).</summary>
    [RelayCommand(CanExecute = nameof(HasMatches))]
    private void ReplaceCurrent()
    {
        if (Replace() is { } edit)
        {
            ApplyEditsRequested?.Invoke(this, new ApplyEditsRequestedEventArgs([edit]));
            Search(SelectionMode.SelectNearest, _pendingAnchor);
            _pendingAnchor = null;
        }
    }

    /// <summary>
    /// Replaces every match (all of them, also beyond the <see cref="TextSearchEngine.MaxMatches"/> the count shows)
    /// as one undo step (Ctrl+Enter in the replace field). Texts over 1,000,000 characters are processed off the UI
    /// thread (<see cref="IsSearching"/>); if the text changes meanwhile, nothing is replaced.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasMatches))]
    private async Task ReplaceAllMatchesAsync()
    {
        if (!IsOpen || Query.Length == 0 || _tab?.Buffer is not { } buffer)
        {
            return;
        }

        var version = buffer.Version;
        var text = SnapshotOf(buffer);
        var options = Options;
        var replacement = Replacement;
        TextReplaceAllResult result;
        if (text.Length <= SynchronousSearchLimit)
        {
            result = TextSearchEngine.ReplaceEveryMatch(text, options, replacement);
        }
        else
        {
            _documentChanged.Cancel();
            _searchCancellation?.Cancel();
            var generation = ++_searchGeneration;
            var cancellation = new CancellationTokenSource();
            _searchCancellation = cancellation;
            IsSearching = true;
            try
            {
                result = await Task.Run(() => TextSearchEngine.ReplaceEveryMatch(text, options, replacement, cancellation.Token), cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                // A newer search (the query or the document changed) replaced this one.
                return;
            }
            finally
            {
                if (generation == _searchGeneration)
                {
                    IsSearching = false;
                }
            }

            if (!IsOpen || !ReferenceEquals(_tab?.Buffer, buffer) || buffer.Version != version)
            {
                _context.Toasts.ShowError("The text changed while NimPad was replacing, so nothing was replaced. Try again.");
                return;
            }
        }

        if (result.Error is { } error)
        {
            UpdateStatus(error);
            return;
        }

        var edits = result.Edits;
        if (edits.Count == 0)
        {
            return;
        }

        ApplyEditsRequested?.Invoke(this, new ApplyEditsRequestedEventArgs(edits));
        Search(SelectionMode.KeepPosition, null);
        _context.Toasts.Show(edits.Count == 1 ? "Replaced 1 match" : $"Replaced {edits.Count.ToString("N0", _context.Culture)} matches");
    }

    /// <summary>The Replace command (current match).</summary>
    public IRelayCommand ReplaceCommand => ReplaceCurrentCommand;

    /// <summary>The Replace all command.</summary>
    public IAsyncRelayCommand ReplaceAllCommand => ReplaceAllMatchesCommand;

    /// <summary>Toggles match case (Alt+C).</summary>
    [RelayCommand]
    private void ToggleMatchCase() => MatchCase = !MatchCase;

    /// <summary>Toggles whole word (Alt+W).</summary>
    [RelayCommand]
    private void ToggleWholeWord() => WholeWord = !WholeWord;

    /// <summary>Toggles regular expressions (Alt+R).</summary>
    [RelayCommand]
    private void ToggleRegex() => UseRegex = !UseRegex;

    /// <summary>Reports a change of the active document; the matches are recomputed 150 ms later.</summary>
    public void OnDocumentChanged()
    {
        if (IsOpen && Query.Length > 0)
        {
            _documentChanged.Schedule();
        }
    }

    /// <summary>Recomputes the matches now (for example after the view applied edits itself).</summary>
    public void Refresh() => Search(SelectionMode.KeepPosition, null);

    /// <summary>
    /// Follows the active tab: its caret is the search anchor, and the matches are recomputed when its document is
    /// attached (a tab that loads after it became active, or a replacement document).
    /// </summary>
    internal void SetActiveTab(TabViewModel? tab)
    {
        if (ReferenceEquals(_tab, tab))
        {
            return;
        }

        if (_tab is not null)
        {
            _tab.PropertyChanged -= OnActiveTabPropertyChanged;
        }

        _tab = tab;
        if (tab is not null)
        {
            tab.PropertyChanged += OnActiveTabPropertyChanged;
        }

        _snapshot = null;
        _snapshotVersion = -1;
        if (IsOpen)
        {
            Search(SelectionMode.KeepPosition, tab?.CaretOffset ?? 0);
        }
    }

    private void OnActiveTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.Buffer) && ReferenceEquals(sender, _tab) && IsOpen)
        {
            Search(SelectionMode.KeepPosition, _tab?.CaretOffset ?? 0);
        }
    }

    // The buffer's text, reusing the last snapshot while the document is unchanged (a large one is expensive to copy).
    private string SnapshotOf(IEditorBuffer buffer) =>
        buffer.Version == _snapshotVersion && _snapshot is not null && ReferenceEquals(buffer, _snapshotBuffer)
            ? _snapshot
            : buffer.GetSnapshotText();

    private TextSearchOptions Options => new(Query, MatchCase, WholeWord, UseRegex);

    private void OpenCore(string? selectedText, bool replace)
    {
        IsReplaceMode = replace;
        var wasOpen = IsOpen;
        IsOpen = true;
        if (!string.IsNullOrEmpty(selectedText) && selectedText.IndexOfAny(['\r', '\n']) < 0)
        {
            _suppressSearch = true;
            Query = selectedText;
            _suppressSearch = false;
        }

        if (!wasOpen || !string.IsNullOrEmpty(selectedText))
        {
            Search(SelectionMode.SelectNearest, _tab?.SelectionStart ?? 0);
        }

        FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    partial void OnQueryChanged(string value) => OnOptionsChanged();

    partial void OnMatchCaseChanged(bool value) => OnOptionsChanged();

    partial void OnWholeWordChanged(bool value) => OnOptionsChanged();

    partial void OnUseRegexChanged(bool value) => OnOptionsChanged();

    private void OnOptionsChanged()
    {
        if (_suppressSearch || !IsOpen)
        {
            return;
        }

        // Typing in the field selects the first match from where the editor's selection (or the current match) starts.
        var anchor = CurrentMatch?.Offset ?? _tab?.SelectionStart ?? 0;
        Search(SelectionMode.SelectNearest, anchor);
    }

    private void Step(int direction)
    {
        if (Matches.Count == 0)
        {
            return;
        }

        int next;
        if (CurrentIndex < 0)
        {
            var caret = _tab?.CaretOffset ?? 0;
            next = direction > 0 ? FirstAtOrAfter(caret) : LastBefore(caret);
        }
        else
        {
            next = ((CurrentIndex + direction) % Matches.Count + Matches.Count) % Matches.Count;
        }

        CurrentIndex = next;
        UpdateStatus(null);
        SelectMatchRequested?.Invoke(this, new SelectMatchRequestedEventArgs(Matches[next]));
    }

    // Makes sure the matches belong to the buffer's current text; recomputes synchronously when they do not.
    private bool EnsureFresh()
    {
        if (_tab?.Buffer is not { } buffer)
        {
            return false;
        }

        if (buffer.Version == _snapshotVersion && _snapshot is not null && ReferenceEquals(buffer, _snapshotBuffer))
        {
            return true;
        }

        // Long texts are searched again in the background; the caller acts on the fresh matches next time.
        Search(SelectionMode.KeepPosition, null);
        return false;
    }

    private void Search(SelectionMode mode, int? anchor)
    {
        _documentChanged.Cancel();
        _searchCancellation?.Cancel();
        var generation = ++_searchGeneration;
        if (!IsOpen || _tab?.Buffer is not { } buffer || Query.Length == 0)
        {
            _snapshot = null;
            _snapshotVersion = -1;
            Apply(TextSearchResult.Empty, mode, anchor);
            return;
        }

        var previousOffset = CurrentMatch?.Offset;
        var version = buffer.Version;
        // Typing in the query reuses the snapshot of an unchanged document.
        var text = SnapshotOf(buffer);
        _snapshotBuffer = buffer;
        var options = Options;
        if (text.Length <= SynchronousSearchLimit)
        {
            _snapshot = text;
            _snapshotVersion = version;
            Apply(TextSearchEngine.Find(text, options), mode, anchor ?? previousOffset);
            return;
        }

        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        IsSearching = true;
        _context.Work.Track(SearchInBackgroundAsync(), "search the document");

        async Task SearchInBackgroundAsync()
        {
            try
            {
                var result = await Task.Run(() => TextSearchEngine.Find(text, options, cancellation.Token), cancellation.Token);
                if (generation == _searchGeneration)
                {
                    _snapshot = text;
                    _snapshotVersion = version;
                    Apply(result, mode, anchor ?? previousOffset);
                }
            }
            catch (OperationCanceledException)
            {
                // A newer search replaced this one.
            }
            finally
            {
                if (generation == _searchGeneration)
                {
                    IsSearching = false;
                }
            }
        }
    }

    private void Apply(TextSearchResult result, SelectionMode mode, int? anchor)
    {
        Matches = result.Matches;
        if (Matches.Count == 0)
        {
            CurrentIndex = -1;
        }
        else
        {
            CurrentIndex = FirstAtOrAfter(anchor ?? 0);
        }

        _truncated = result.IsTruncated;
        UpdateStatus(result.Error);
        if (mode == SelectionMode.SelectNearest && CurrentMatch is { } current)
        {
            SelectMatchRequested?.Invoke(this, new SelectMatchRequestedEventArgs(current));
        }
    }

    private int FirstAtOrAfter(int offset)
    {
        var lo = 0;
        var hi = Matches.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (Matches[mid].Offset < offset)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo < Matches.Count ? lo : 0;
    }

    private int LastBefore(int offset)
    {
        var first = FirstAtOrAfter(offset);
        if (Matches[first].Offset >= offset)
        {
            return first == 0 ? Matches.Count - 1 : first - 1;
        }

        return Matches.Count - 1;
    }

    private void UpdateStatus(string? error)
    {
        HasError = error is not null;
        if (error is not null)
        {
            StatusText = error;
        }
        else if (Query.Length == 0 || !IsOpen)
        {
            StatusText = string.Empty;
        }
        else if (Matches.Count == 0)
        {
            StatusText = "No results";
        }
        else
        {
            var total = Matches.Count.ToString(CultureInfo.InvariantCulture) + (_truncated ? "+" : string.Empty);
            StatusText = CurrentIndex >= 0
                ? string.Create(CultureInfo.InvariantCulture, $"{CurrentIndex + 1} of {total}")
                : $"{total} results";
        }
    }
}
