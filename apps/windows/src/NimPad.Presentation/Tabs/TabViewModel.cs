using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NimPad.Core.Domain;
using NimPad.Core.Files;
using NimPad.Core.Ids;
using NimPad.Core.Services;
using NimPad.Presentation.Feedback;
using NimPad.Presentation.Infrastructure;
using NimPad.Presentation.Platform;

namespace NimPad.Presentation.Tabs;

/// <summary>What a tab shows.</summary>
public enum TabKind
{
    /// <summary>A managed note in the workspace (autosaved).</summary>
    Managed,

    /// <summary>A file on disk, saved with Ctrl+S.</summary>
    External,
}

/// <summary>Where a tab's text is.</summary>
public enum TabLoadState
{
    /// <summary>Not requested yet (restored tabs load on first activation).</summary>
    NotLoaded,

    /// <summary>Being read.</summary>
    Loading,

    /// <summary>Read; <see cref="TabViewModel.LoadAsync"/> returns it until a buffer is attached.</summary>
    Loaded,

    /// <summary>Reading failed; see <see cref="TabViewModel.LoadError"/>.</summary>
    Failed,
}

/// <summary>The tab's save indicator.</summary>
public enum TabSaveState
{
    /// <summary>Everything is stored (no dot).</summary>
    Saved,

    /// <summary>
    /// Changes are waiting (the accent dot): an external file with unsaved changes, or a note whose autosave has been
    /// pending for more than <see cref="TabViewModel.PendingIndicatorDelay"/> (quick saves never flash the dot).
    /// </summary>
    Pending,

    /// <summary>The last autosave failed; it is retried ("Not saved · Retry").</summary>
    Failed,
}

/// <summary>The App replaces the whole text of the tab's document (reload from disk or from the store).</summary>
public sealed class ReplaceTextRequestedEventArgs(string text) : EventArgs
{
    /// <summary>The new text.</summary>
    public string Text { get; } = text;
}

/// <summary>
/// One tab of the strip: a managed note or an external file, its load and save state, its inline rename and the
/// editor view state (caret, selection, scroll) that the session keeps.
/// </summary>
/// <remarks>
/// <para><b>Text lifecycle (the App's part).</b> When the tab becomes active and the App has no document for it,
/// it awaits <see cref="LoadAsync"/> (the same task while a load is running; the shell starts the active tab's load at
/// startup), builds a <c>TextDocument</c> from the text, wraps it in an <see cref="IEditorBuffer"/>, calls
/// <see cref="AttachBuffer"/> and restores <see cref="CaretOffset"/>, the selection and the scroll offsets. From then
/// on it calls <see cref="OnBufferChanged"/> on every document change and <see cref="UpdateViewState"/> when the caret
/// or the scroll position changes, and handles <see cref="ReplaceTextRequested"/>.</para>
/// <para><b>Managed notes</b> are saved by Core's <c>AutosaveScheduler</c>. <b>External files</b> track dirty state by
/// buffer version, write a recovery buffer 2 s after the last change and are saved by <see cref="SaveAsync"/>.</para>
/// </remarks>
public sealed partial class TabViewModel : ObservableObject
{
    /// <summary>How long an autosave must be pending before the tab shows its dot.</summary>
    public static readonly TimeSpan PendingIndicatorDelay = TimeSpan.FromMilliseconds(600);

    /// <summary>How long a load runs before the skeleton placeholder shows (design page 29).</summary>
    public static readonly TimeSpan LoadingPlaceholderDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>How long a load runs before a spinner shows (design page 29).</summary>
    public static readonly TimeSpan LoadingSpinnerDelay = TimeSpan.FromMilliseconds(600);

    /// <summary>The quiet period before an external file's unsaved text is written to its recovery buffer.</summary>
    public static readonly TimeSpan RecoveryBufferDelay = TimeSpan.FromSeconds(2);

    private readonly TabsViewModel _owner;
    private readonly ViewModelContext _context;
    private readonly Debouncer _recoveryWriter;
    private Task<string>? _loadTask;
    private CancellationTokenSource? _loadCancellation;
    private BufferSource? _source;
    private string? _baseChecksum;
    private long _savedVersion;
    private bool _startsDirty;
    private RecoveryBuffer? _pendingRecovery;
    private DateTime? _diskWriteTimeUtc;
    private ITimer? _pendingTimer;
    private ITimer? _placeholderTimer;
    private long _loadGeneration;

    internal TabViewModel(TabsViewModel owner, ViewModelContext context, Guid tabId, TabKind kind, NodeId? nodeId, string? filePath, string title)
    {
        _owner = owner;
        _context = context;
        TabId = tabId;
        Kind = kind;
        NodeId = nodeId;
        FilePath = filePath;
        Title = title;
        ToolTip = filePath ?? title;
        _recoveryWriter = new Debouncer(context.Clock, context.Dispatcher, RecoveryBufferDelay, WriteRecoveryBuffer);
        if (kind == TabKind.External && filePath is not null)
        {
            Format = DocumentFormats.FromName(Path.GetFileName(filePath));
        }
    }

    /// <summary>The tab's stable ID (kept in the session).</summary>
    public Guid TabId { get; }

    /// <summary>Managed note or external file.</summary>
    public TabKind Kind { get; }

    /// <summary>True for a managed note.</summary>
    public bool IsManaged => Kind == TabKind.Managed;

    /// <summary>True for an external file.</summary>
    public bool IsExternal => Kind == TabKind.External;

    /// <summary>The note, for a managed tab.</summary>
    public NodeId? NodeId { get; }

    /// <summary>The file's full path, for an external tab (it changes after Save As).</summary>
    [ObservableProperty]
    public partial string? FilePath { get; private set; }

    /// <summary>The title: the note's name (kept in sync with renames) or the file name.</summary>
    [ObservableProperty]
    public partial string Title { get; internal set; }

    /// <summary>The tooltip: the note's breadcrumb path (<c>My Workspace / Projects / Plan</c>) or the file path.</summary>
    [ObservableProperty]
    public partial string ToolTip { get; internal set; }

    /// <summary>Pinned tabs sit at the left edge, are narrower and have no close button.</summary>
    [ObservableProperty]
    public partial bool IsPinned { get; internal set; }

    /// <summary>True for the selected (active) tab.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; internal set; }

    /// <summary>Where the text is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading))]
    public partial TabLoadState LoadState { get; private set; }

    /// <summary>
    /// True from the moment a load starts until it ends (for logic and a muted title). Do not bind the tab's spinner
    /// to it: a fast load would flash it. Bind <see cref="IsLoadingSpinnerVisible"/> (after 600 ms) and the editor's
    /// skeleton to <see cref="IsLoadingPlaceholderVisible"/> (after 300 ms) instead.
    /// </summary>
    public bool IsLoading => LoadState == TabLoadState.Loading;

    /// <summary>Why the load failed, for the error placeholder.</summary>
    [ObservableProperty]
    public partial string? LoadError { get; private set; }

    /// <summary>True once a load has run for <see cref="LoadingPlaceholderDelay"/> (show skeleton lines).</summary>
    [ObservableProperty]
    public partial bool IsLoadingPlaceholderVisible { get; private set; }

    /// <summary>True once a load has run for <see cref="LoadingSpinnerDelay"/> (bind the tab's spinner to it).</summary>
    [ObservableProperty]
    public partial bool IsLoadingSpinnerVisible { get; private set; }

    /// <summary>True while a file over 5 MB loads (the design's "Opening …" panel with progress and Cancel).</summary>
    [ObservableProperty]
    public partial bool IsLoadProgressVisible { get; private set; }

    /// <summary>The read progress, 0–1, for <see cref="IsLoadProgressVisible"/>.</summary>
    [ObservableProperty]
    public partial double LoadProgress { get; private set; }

    /// <summary>The large-file panel's title: <c>Opening “server.log”</c>.</summary>
    public string LoadingText => "Opening " + DisplayText.Quote(Title);

    /// <summary>The large-file panel's size line, for example <c>12.4 MB</c> (empty when unknown).</summary>
    [ObservableProperty]
    public partial string LoadingSizeText { get; private set; } = string.Empty;

    /// <summary>The save indicator.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsPendingDot))]
    public partial TabSaveState SaveState { get; private set; }

    /// <summary>True when the tab shows its accent dot.</summary>
    public bool ShowsPendingDot => SaveState != TabSaveState.Saved;

    /// <summary>
    /// True when the text has changes that are not stored: unsaved edits of an external file, or a note whose autosave
    /// has not committed yet.
    /// </summary>
    [ObservableProperty]
    public partial bool IsDirty { get; private set; }

    /// <summary>True while the tab title is an inline text box (F2).</summary>
    [ObservableProperty]
    public partial bool IsRenaming { get; private set; }

    /// <summary>The inline rename text.</summary>
    [ObservableProperty]
    public partial string RenameText { get; set; } = string.Empty;

    /// <summary>Why the typed name is not accepted (shown inline), or <see langword="null"/>.</summary>
    [ObservableProperty]
    public partial string? RenameError { get; private set; }

    /// <summary>Whether the tab can be renamed (managed notes).</summary>
    public bool CanRename => IsManaged;

    /// <summary>The syntax mode.</summary>
    [ObservableProperty]
    public partial DocumentFormat Format { get; internal set; }

    /// <summary>The file's encoding (external files; <see langword="null"/> until loaded).</summary>
    [ObservableProperty]
    public partial TextEncodingInfo? Encoding { get; private set; }

    /// <summary>The file's line endings (external files; <see langword="null"/> until loaded).</summary>
    [ObservableProperty]
    public partial LineEndingInfo? LineEnding { get; private set; }

    /// <summary>The caret offset (restored when the tab's document is shown).</summary>
    [ObservableProperty]
    public partial int CaretOffset { get; private set; }

    /// <summary>The selection start offset.</summary>
    [ObservableProperty]
    public partial int SelectionStart { get; private set; }

    /// <summary>The selection length.</summary>
    [ObservableProperty]
    public partial int SelectionLength { get; private set; }

    /// <summary>The vertical scroll offset in device-independent pixels.</summary>
    [ObservableProperty]
    public partial double VerticalOffset { get; private set; }

    /// <summary>The horizontal scroll offset in device-independent pixels.</summary>
    [ObservableProperty]
    public partial double HorizontalOffset { get; private set; }

    /// <summary>The text length (from the buffer once attached).</summary>
    [ObservableProperty]
    public partial int TextLength { get; internal set; }

    /// <summary>True once the user changed the text in this session.</summary>
    [ObservableProperty]
    public partial bool HasBeenEdited { get; private set; }

    /// <summary>True for a note created in this session (an untouched one is discarded when its tab closes).</summary>
    public bool IsCreatedThisSession { get; internal init; }

    /// <summary>
    /// True when closing the tab deletes its note for good if Core finds it pristine (an empty <c>Untitled N</c> note
    /// nobody touched): notes created in this session and tabs carried over from the last session. A pristine note the
    /// user opened from the sidebar is kept.
    /// </summary>
    internal bool DiscardsWhenPristine { get; init; }

    /// <summary>True while the tab is closing (guards against a second close).</summary>
    internal bool IsClosing { get; set; }

    /// <summary>The live text, once attached (raises <c>PropertyChanged</c> when a document is attached).</summary>
    public IEditorBuffer? Buffer { get; private set; }

    /// <summary>
    /// The last write time NimPad itself gave the file (from <see cref="SaveAsync"/>), so the App can ignore the
    /// file watcher's echo of its own save.
    /// </summary>
    public DateTime? LastSavedWriteTimeUtc { get; private set; }

    /// <summary>Raised when the App must replace the document's text (reload from disk, recovered text).</summary>
    public event EventHandler<ReplaceTextRequestedEventArgs>? ReplaceTextRequested;

    // ------------------------------------------------------------------ loading

    /// <summary>
    /// Returns the tab's text: from the attached buffer, from a load in progress or done (until a buffer is
    /// attached), or by starting a load (managed: <c>DocumentService</c>; external: <c>ExternalFileService</c>, with
    /// progress for files over 5 MB and <see cref="CancelLoadCommand"/>).
    /// </summary>
    /// <exception cref="OperationCanceledException">The load was cancelled (the tab closes).</exception>
    /// <exception cref="Exception">The load failed; <see cref="LoadState"/> is <see cref="TabLoadState.Failed"/>.</exception>
    public Task<string> LoadAsync()
    {
        if (Buffer is { } buffer)
        {
            return Task.FromResult(buffer.GetSnapshotText());
        }

        if (_loadTask is { } existing && !existing.IsFaulted && !existing.IsCanceled)
        {
            return existing;
        }

        _loadTask = LoadCoreAsync();
        return _loadTask;
    }

    /// <summary>Cancels a running load; the tab is closed.</summary>
    [RelayCommand]
    private void CancelLoad() => _loadCancellation?.Cancel();

    /// <summary>Retries a failed load.</summary>
    [RelayCommand]
    private async Task RetryLoadAsync()
    {
        try
        {
            await LoadAsync();
        }
        catch (Exception)
        {
            // Shown through LoadState and LoadError.
        }
    }

    private async Task<string> LoadCoreAsync()
    {
        var generation = ++_loadGeneration;
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        var token = _loadCancellation.Token;
        LoadState = TabLoadState.Loading;
        LoadError = null;
        StartPlaceholderTimer(generation);
        try
        {
            string text;
            if (IsManaged)
            {
                var content = await _context.Core.Documents.GetContentAsync(NodeId!.Value, token);
                _baseChecksum = content.Checksum;
                Format = content.Format;
                text = content.Text;
            }
            else if (_pendingRecovery is { } recovery)
            {
                text = recovery.Text;
                Encoding = recovery.Encoding;
                LineEnding = LineEndingInfo.Detect(recovery.Text);
                _diskWriteTimeUtc = recovery.DiskLastWriteTimeUtc;
                _startsDirty = true;
                _pendingRecovery = null;
            }
            else
            {
                var path = FilePath!;
                try
                {
                    var length = new FileInfo(path).Length;
                    LoadingSizeText = DisplayText.FileSize(length, _context.Culture);
                    IsLoadProgressVisible = length > ExternalFileService.ProgressThreshold;
                }
                catch (IOException)
                {
                    // OpenAsync reports the problem.
                }

                var progress = new UiProgress(this, generation);
                var file = await _context.Core.ExternalFiles.OpenAsync(path, progress, token);

                // A Cancel that arrived while the read was finishing still closes the tab, as the user asked.
                token.ThrowIfCancellationRequested();
                text = file.Text;
                Encoding = file.Encoding;
                LineEnding = file.LineEnding;
                _diskWriteTimeUtc = file.LastWriteTimeUtc;
                _startsDirty = false;
            }

            if (generation == _loadGeneration)
            {
                TextLength = text.Length;
                LoadState = TabLoadState.Loaded;
            }

            return text;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (generation == _loadGeneration)
            {
                LoadState = TabLoadState.NotLoaded;
                _loadTask = null;
                _owner.OnLoadCancelled(this);
            }

            throw;
        }
        catch (Exception ex)
        {
            if (generation == _loadGeneration)
            {
                LoadError = DescribeLoadError(ex);
                LoadState = TabLoadState.Failed;
            }

            throw;
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                StopPlaceholderTimer();
                IsLoadProgressVisible = false;
            }
        }
    }

    /// <summary>
    /// Connects the live document. Managed notes start autosaving (based on the loaded text); external files start
    /// tracking their dirty state. Attach once per load; the text from <see cref="LoadAsync"/> is released.
    /// </summary>
    public void AttachBuffer(IEditorBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (ReferenceEquals(Buffer, buffer))
        {
            return;
        }

        var previous = _source;
        var wasDirty = IsDirty;
        Buffer = buffer;
        _loadTask = null;
        LoadState = TabLoadState.Loaded;
        TextLength = buffer.TextLength;
        if (previous is null)
        {
            _source = new BufferSource(buffer, 0);
            if (IsManaged)
            {
                _context.Core.Autosave.Track(NodeId!.Value, _source, _baseChecksum);
                UpdateManagedDirty();
            }
            else
            {
                _savedVersion = _startsDirty ? long.MinValue : buffer.Version;
                UpdateExternalDirty();
                if (_startsDirty)
                {
                    _recoveryWriter.Schedule();
                    WarnIfChangedSinceRecovery();
                }
            }

            OnPropertyChanged(nameof(Buffer));
            return;
        }

        // A replacement document (built from this tab's text) continues the previous version sequence, so the
        // autosave scheduler sees it as a newer change and nothing typed before is taken for saved.
        _source = new BufferSource(buffer, previous.Version + 1 - buffer.Version);
        if (IsManaged)
        {
            _context.Core.Autosave.NotifyChanged(NodeId!.Value, _source);
            UpdateManagedDirty();
        }
        else
        {
            _savedVersion = wasDirty ? long.MinValue : buffer.Version;
            UpdateExternalDirty();
        }

        OnPropertyChanged(nameof(Buffer));
    }

    /// <summary>
    /// Reports a change of the live document (call it from the document's change event; cheap). Changes after
    /// <see cref="Shell.ShellViewModel.ShutdownAsync"/> closed the database are ignored, so make the editors read-only
    /// while the shell shuts down (<see cref="Shell.ShellViewModel.IsShuttingDown"/>).
    /// </summary>
    public void OnBufferChanged()
    {
        if (Buffer is not { } buffer || _source is null || !_context.IsReady)
        {
            return;
        }

        HasBeenEdited = true;
        TextLength = buffer.TextLength;
        _owner.OnTabBufferChanged(this);
        if (IsManaged)
        {
            _context.Core.Autosave.NotifyChanged(NodeId!.Value, _source);
            IsDirty = true;
            if (SaveState == TabSaveState.Saved)
            {
                StartPendingTimer();
            }
        }
        else
        {
            UpdateExternalDirty();
            if (IsDirty)
            {
                _recoveryWriter.Schedule();
            }
        }
    }

    /// <summary>Records the editor's view state (caret, selection, scroll) for the session.</summary>
    public void UpdateViewState(int caretOffset, int selectionStart, int selectionLength, double verticalOffset, double horizontalOffset)
    {
        if (CaretOffset == caretOffset && SelectionStart == selectionStart && SelectionLength == selectionLength
            && VerticalOffset.Equals(verticalOffset) && HorizontalOffset.Equals(horizontalOffset))
        {
            return;
        }

        CaretOffset = Math.Max(0, caretOffset);
        SelectionStart = Math.Max(0, selectionStart);
        SelectionLength = Math.Max(0, selectionLength);
        VerticalOffset = double.IsFinite(verticalOffset) ? Math.Max(0, verticalOffset) : 0;
        HorizontalOffset = double.IsFinite(horizontalOffset) ? Math.Max(0, horizontalOffset) : 0;
        _owner.ScheduleSessionSave();
    }

    // ------------------------------------------------------------------ rename

    /// <summary>Starts the inline rename (F2) of a managed note.</summary>
    public void BeginRename()
    {
        if (!CanRename)
        {
            return;
        }

        RenameText = Title;
        RenameError = null;
        IsRenaming = true;
    }

    /// <summary>Renames the note to <see cref="RenameText"/>; a rejected name keeps the box open with <see cref="RenameError"/>.</summary>
    [RelayCommand]
    private async Task CommitRenameAsync()
    {
        if (!IsRenaming || NodeId is not { } nodeId)
        {
            return;
        }

        if (string.Equals(RenameText.Trim(), Title, StringComparison.Ordinal))
        {
            CancelRename();
            return;
        }

        try
        {
            var renamed = await _context.Core.Nodes.RenameAsync(nodeId, RenameText);
            _context.Model.Upsert(renamed, NodeChangeKind.Renamed);
            Title = renamed.Name;
            IsRenaming = false;
            RenameError = null;
        }
        catch (InvalidNodeNameException ex)
        {
            RenameError = ex.Reason;
        }
        catch (NodeOperationException ex)
        {
            RenameError = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A command must not throw into the dispatcher; show it where the user is looking.
            RenameError = ViewModelContext.UserMessage(ex);
        }
    }

    /// <summary>Leaves the inline rename without changing the name (Esc).</summary>
    [RelayCommand]
    public void CancelRename()
    {
        IsRenaming = false;
        RenameError = null;
    }

    partial void OnRenameTextChanged(string value) => RenameError = null;

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(LoadingText));

    // ------------------------------------------------------------------ external files

    /// <summary>
    /// Saves an external file in place, in its encoding and with its line endings, and records the new write time
    /// (<see cref="LastSavedWriteTimeUtc"/>). Failures show an error banner.
    /// </summary>
    /// <returns><see langword="true"/> when the file was saved (or had nothing to save).</returns>
    public async Task<bool> SaveAsync()
    {
        if (!IsExternal || FilePath is not { } path)
        {
            return false;
        }

        if (Buffer is not { } buffer)
        {
            return true;
        }

        return await SaveToAsync(path, buffer);
    }

    /// <summary>Saves an external file under a new path and makes the tab show that file.</summary>
    /// <returns><see langword="true"/> when the file was saved.</returns>
    public async Task<bool> SaveAsAsync(string newPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newPath);
        if (!IsExternal || Buffer is not { } buffer)
        {
            return false;
        }

        var oldPath = FilePath;
        var fullPath = System.IO.Path.GetFullPath(newPath);
        if (!await SaveToAsync(fullPath, buffer))
        {
            return false;
        }

        if (!string.Equals(oldPath, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            FilePath = fullPath;
            Title = System.IO.Path.GetFileName(fullPath);
            ToolTip = fullPath;
            Format = DocumentFormats.FromName(Title);
            if (oldPath is not null)
            {
                Track(_context.Core.ExternalFiles.ClearRecoveryBufferAsync(oldPath), "clear the old recovery buffer");
            }

            _owner.ScheduleSessionSave();
        }

        return true;
    }

    private async Task<bool> SaveToAsync(string path, IEditorBuffer buffer)
    {
        var version = buffer.Version;
        var text = buffer.GetSnapshotText();
        var encoding = Encoding ?? TextEncodingInfo.Utf8;
        var bannerKey = SaveBannerKey;
        try
        {
            var written = await _context.Core.ExternalFiles.SaveAsync(path, text, encoding);
            LastSavedWriteTimeUtc = written;
            _diskWriteTimeUtc = written;
            if (version > _savedVersion || _savedVersion == long.MinValue)
            {
                _savedVersion = version;
            }

            _startsDirty = false;
            IsMissingOnDisk = false;
            _recoveryWriter.Cancel();
            UpdateExternalDirty();
            _context.Banners.Remove(bannerKey);
            _context.Banners.Remove(DiskBannerKey);
            Track(_context.Core.ExternalFiles.ClearRecoveryBufferAsync(path), "clear the recovery buffer");
            return true;
        }
        catch (UnrepresentableCharactersException ex)
        {
            _context.Banners.Show(
                bannerKey,
                BannerKind.Error,
                $"{DisplayText.Quote(Title)} wasn't saved: it contains characters that {ex.Encoding.DisplayName} can't store.");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _context.Banners.Show(
                bannerKey,
                BannerKind.Error,
                $"Couldn't save {DisplayText.Quote(Title)}. {ex.Message}",
                "Retry",
                async () =>
                {
                    if (!await SaveAsync())
                    {
                        throw new IOException("The file still can't be saved.");
                    }
                });
            return false;
        }
    }

    /// <summary>
    /// Tells the tab its file changed on disk (from the App's file watcher). A change NimPad made itself
    /// (<see cref="LastSavedWriteTimeUtc"/>) is ignored; otherwise a banner offers Reload, and dismissing it keeps the
    /// text in the tab.
    /// </summary>
    /// <param name="lastWriteTimeUtc">The file's new last write time, or <see langword="null"/> to read it.</param>
    /// <returns><see langword="true"/> when the banner is shown.</returns>
    public bool OnFileChangedOnDisk(DateTime? lastWriteTimeUtc = null)
    {
        if (!IsExternal || FilePath is not { } path)
        {
            return false;
        }

        DateTime written;
        try
        {
            if (lastWriteTimeUtc is null && !File.Exists(path))
            {
                return OnFileMissingOnDisk();
            }

            written = lastWriteTimeUtc ?? File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (IsMissingOnDisk)
        {
            // The file is back (moved back, restored or recreated): the text is unsaved only if it was edited.
            IsMissingOnDisk = false;
            _context.Banners.Remove(DiskBannerKey);
            UpdateExternalDirty();
        }

        if (written == LastSavedWriteTimeUtc || written == _diskWriteTimeUtc)
        {
            return false;
        }

        if (Buffer is null)
        {
            // Not shown yet: the next load reads the new content (recovered unsaved text is kept; its tab warns about
            // the disk change once it is shown).
            if (LoadState == TabLoadState.Loaded && !_startsDirty)
            {
                _loadTask = null;
                LoadState = TabLoadState.NotLoaded;
            }

            return false;
        }

        ShowChangedOnDiskBanner();
        return true;
    }

    private void ShowChangedOnDiskBanner() =>
        _context.Banners.Show(
            DiskBannerKey,
            BannerKind.Warning,
            $"{DisplayText.Quote(Title)} changed on disk.",
            "Reload",
            ReloadFromDiskAsync);

    /// <summary>
    /// True while the tab's file is missing on disk (moved, renamed or deleted by another program). The text stays in
    /// the tab and counts as unsaved: Save writes the file back, Save as puts it elsewhere.
    /// </summary>
    [ObservableProperty]
    public partial bool IsMissingOnDisk { get; private set; }

    /// <summary>
    /// Tells the tab its file was moved, renamed or deleted (from the App's file watcher). A shown tab keeps its text,
    /// becomes unsaved and shows "“X” was moved or deleted." with Save as; a tab whose text is not shown yet forgets a
    /// clean loaded copy, so showing it reports the missing file.
    /// </summary>
    /// <returns><see langword="true"/> when the banner is shown.</returns>
    public bool OnFileMissingOnDisk()
    {
        if (!IsExternal || FilePath is not { } path)
        {
            return false;
        }

        try
        {
            if (File.Exists(path))
            {
                // Back already (an atomic save replaces the file): treat it as a change.
                return OnFileChangedOnDisk(File.GetLastWriteTimeUtc(path));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (Buffer is null)
        {
            if (LoadState == TabLoadState.Loaded && !_startsDirty)
            {
                _loadTask = null;
                LoadState = TabLoadState.NotLoaded;
            }

            return false;
        }

        IsMissingOnDisk = true;
        UpdateExternalDirty();
        _context.Banners.Show(
            DiskBannerKey,
            BannerKind.Warning,
            $"{DisplayText.Quote(Title)} was moved or deleted.",
            "Save as",
            SaveAsFromBannerAsync,
            removeOnAction: false);
        return true;
    }

    // The missing-file banner's Save as: the dialog starts in the file's folder when it still exists.
    private async Task SaveAsFromBannerAsync()
    {
        if (FilePath is not { } path)
        {
            return;
        }

        var directory = System.IO.Path.GetDirectoryName(path);
        var target = _context.Platform.FileDialogs.PickSaveFile(
            System.IO.Path.GetFileName(path),
            directory is not null && Directory.Exists(directory) ? directory : null);
        if (!string.IsNullOrWhiteSpace(target))
        {
            await SaveAsAsync(target);
        }
    }

    /// <summary>Replaces the tab's text with the file's current content (the banner's Reload).</summary>
    public async Task ReloadFromDiskAsync()
    {
        if (!IsExternal || FilePath is not { } path)
        {
            return;
        }

        var file = await _context.Core.ExternalFiles.OpenAsync(path);
        Encoding = file.Encoding;
        LineEnding = file.LineEnding;
        _diskWriteTimeUtc = file.LastWriteTimeUtc;
        _context.Banners.Remove(DiskBannerKey);
        if (Buffer is not { } buffer)
        {
            _loadTask = Task.FromResult(file.Text);
            LoadState = TabLoadState.Loaded;
            return;
        }

        ReplaceTextRequested?.Invoke(this, new ReplaceTextRequestedEventArgs(file.Text));
        _savedVersion = buffer.Version;
        _startsDirty = false;
        _recoveryWriter.Cancel();
        TextLength = buffer.TextLength;
        UpdateExternalDirty();
        Track(_context.Core.ExternalFiles.ClearRecoveryBufferAsync(path), "clear the recovery buffer");
    }

    /// <summary>
    /// Copies the file into the workspace as a managed note (its current text, including unsaved edits) and opens the
    /// note in a new tab. Nothing is uploaded.
    /// </summary>
    /// <returns>The new note's tab.</returns>
    public async Task<TabViewModel?> ImportAsync()
    {
        if (!IsExternal || FilePath is not { } path)
        {
            return null;
        }

        var files = _context.Core.ExternalFiles;
        var node = Buffer is { } buffer
            ? await files.ImportTextAsync(System.IO.Path.GetFileName(path), buffer.GetSnapshotText())
            : await files.ImportAsync(path);
        _context.Model.Upsert(node, NodeChangeKind.Created);
        var tab = await _owner.OpenAsync(node.Id);
        _context.Toasts.Show($"Imported {DisplayText.Quote(node.Name)} into {_context.Workspace.Name}.", ToastKind.Success);
        return tab;
    }

    /// <summary>
    /// Makes the tab start from recovered unsaved text instead of the file's content (crash recovery). Called when the
    /// tab is created, before its first load, so the recovered text is what the user sees and edits: nothing typed
    /// later can overwrite the recovery buffer with the disk text plus new edits.
    /// </summary>
    internal void StartFromRecovery(RecoveryBuffer recovery)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        if (Buffer is not null || LoadState != TabLoadState.NotLoaded)
        {
            throw new InvalidOperationException("Recovered text can only be applied before the tab loads.");
        }

        _pendingRecovery = recovery;
    }

    // The file changed on disk after the crash that left the recovered text: say so, so saving does not silently
    // replace the newer file.
    private void WarnIfChangedSinceRecovery()
    {
        if (FilePath is not { } path || _diskWriteTimeUtc is null)
        {
            return;
        }

        DateTime written;
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            written = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing to compare with.
            return;
        }

        // The recovery buffer keeps the time to the millisecond.
        if (written.Ticks / TimeSpan.TicksPerMillisecond == _diskWriteTimeUtc.Value.Ticks / TimeSpan.TicksPerMillisecond)
        {
            _diskWriteTimeUtc = written;
            return;
        }

        ShowChangedOnDiskBanner();
    }

    /// <summary>Writes the external file's unsaved text to its recovery buffer now (shutdown, crash handlers).</summary>
    internal Task FlushRecoveryBufferAsync()
    {
        _recoveryWriter.Cancel();
        return IsExternal && IsDirty && Buffer is not null ? WriteRecoveryBufferAsync() : Task.CompletedTask;
    }

    /// <summary>Forgets the unsaved changes (the user chose "Don't save" while closing the window).</summary>
    internal void MarkDiscarded()
    {
        _recoveryWriter.Cancel();
        IsMissingOnDisk = false;
        if (Buffer is { } buffer)
        {
            _savedVersion = buffer.Version;
            _startsDirty = false;
        }

        UpdateExternalDirty();
    }

    /// <summary>Discards the recovery buffer (the user chose not to save).</summary>
    internal Task DiscardRecoveryBufferAsync()
    {
        _recoveryWriter.Cancel();
        return FilePath is { } path ? _context.Core.ExternalFiles.ClearRecoveryBufferAsync(path) : Task.CompletedTask;
    }

    private void WriteRecoveryBuffer()
    {
        // The debounce can fire after shutdown closed the database; the shutdown wrote the buffer already.
        if (_context.IsReady)
        {
            Track(WriteRecoveryBufferAsync(), "write a recovery buffer");
        }
    }

    // Snapshots on the calling thread; failures (including the snapshot's) end up in the returned task.
    private async Task WriteRecoveryBufferAsync()
    {
        if (Buffer is not { } buffer || FilePath is not { } path || !IsDirty)
        {
            return;
        }

        var text = buffer.GetSnapshotText();
        await _context.Core.ExternalFiles.SaveRecoveryBufferAsync(
            path,
            text,
            Encoding ?? TextEncodingInfo.Utf8,
            LineEnding?.Style ?? LineEndingStyle.Crlf,
            _diskWriteTimeUtc).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ managed notes: autosave

    internal string? BaseChecksum => _baseChecksum;

    internal void OnAutosaveSucceeded(AutosaveSucceededEventArgs args)
    {
        _baseChecksum = args.Result.Checksum;
        UpdateManagedDirty();
        if (!IsDirty)
        {
            StopPendingTimer();
            SaveState = TabSaveState.Saved;
        }
        else if (SaveState == TabSaveState.Failed)
        {
            SaveState = TabSaveState.Pending;
        }

        _context.Banners.Remove(SaveBannerKey);
    }

    internal void OnAutosaveFailed(AutosaveFailedEventArgs args)
    {
        UpdateManagedDirty();
        StopPendingTimer();
        SaveState = TabSaveState.Failed;
        if (args.WillRetry)
        {
            _context.Banners.Show(
                SaveBannerKey,
                BannerKind.Error,
                $"Couldn't save {DisplayText.Quote(Title)}. NimPad keeps trying; your text stays in this tab.",
                "Retry",
                RetrySaveAsync,
                canDismiss: false);
            return;
        }

        var message = args.Error is DocumentConflictException
            ? $"{DisplayText.Quote(Title)} was changed somewhere else, so your edits were not saved over it."
            : $"{DisplayText.Quote(Title)} no longer exists, so your edits can't be saved there.";
        _context.Banners.Show(SaveBannerKey, BannerKind.Error, message, "Save as a new note", SaveAsNewNoteAsync, canDismiss: false);
    }

    /// <summary>Saves the note now (the status bar's Retry).</summary>
    public async Task RetrySaveAsync()
    {
        if (!IsManaged || NodeId is not { } nodeId)
        {
            return;
        }

        await _context.Core.Autosave.FlushAsync(nodeId);
    }

    /// <summary>
    /// Keeps the tab's text as a new note next to the original (after a conflict, or when the note no longer exists)
    /// and opens it; this tab then shows the stored note again, or closes when the note is gone.
    /// </summary>
    internal async Task SaveAsNewNoteAsync()
    {
        if (!IsManaged || NodeId is not { } nodeId || Buffer is not { } buffer)
        {
            return;
        }

        var text = buffer.GetSnapshotText();
        var original = _context.Model.Get(nodeId) ?? await _context.Core.Nodes.GetNodeAsync(nodeId);
        var parentId = original is { IsTrashed: false } && original.ParentId is { } parent && _context.Model.Contains(parent) ? parent : (NodeId?)null;
        var siblings = _context.Model.GetChildren(parentId).Select(node => node.Name);
        var name = NodeNames.CopyName(original?.Name ?? Title, NodeKind.Document, siblings);
        var created = await _context.Core.Nodes.CreateDocumentAsync(_context.Workspace.Id, parentId, name, original?.Format ?? Format, text);
        _context.Model.Upsert(created, NodeChangeKind.Created);
        _context.Core.Autosave.Remove(nodeId);
        _context.Banners.Remove(SaveBannerKey);
        SaveState = TabSaveState.Saved;
        IsDirty = false;
        DocumentContent? stored = null;
        if (original is { IsTrashed: false } && _context.Model.Contains(nodeId))
        {
            try
            {
                stored = await _context.Core.Documents.GetContentAsync(nodeId);
            }
            catch (NodeNotFoundException)
            {
                // Deleted before the sidebar heard about it.
            }
        }

        if (stored is null)
        {
            await _owner.CloseWithoutSavingAsync(this);
        }
        else
        {
            _baseChecksum = stored.Checksum;
            _context.Core.Autosave.Track(nodeId, _source!, stored.Checksum);
            ReplaceTextRequested?.Invoke(this, new ReplaceTextRequestedEventArgs(stored.Text));
            TextLength = buffer.TextLength;
        }

        await _owner.OpenAsync(created.Id);
    }

    private void UpdateManagedDirty()
    {
        if (NodeId is { } nodeId && _context.IsReady)
        {
            IsDirty = _context.Core.Autosave.IsDirty(nodeId);
        }
    }

    private void UpdateExternalDirty()
    {
        var dirty = Buffer is { } buffer && (buffer.Version != _savedVersion || IsMissingOnDisk);
        IsDirty = dirty;
        SaveState = dirty ? TabSaveState.Pending : TabSaveState.Saved;
    }

    private void StartPendingTimer()
    {
        if (_pendingTimer is not null)
        {
            return;
        }

        _pendingTimer = _context.Clock.CreateTimer(
            _ => _context.Dispatcher.Post(() =>
            {
                if (_pendingTimer is null)
                {
                    return;
                }

                StopPendingTimer();
                UpdateManagedDirty();
                if (IsDirty && SaveState == TabSaveState.Saved)
                {
                    SaveState = TabSaveState.Pending;
                }
            }),
            null,
            PendingIndicatorDelay,
            Timeout.InfiniteTimeSpan);
    }

    private void StopPendingTimer()
    {
        _pendingTimer?.Dispose();
        _pendingTimer = null;
    }

    // ------------------------------------------------------------------ helpers

    internal string SaveBannerKey => "save:" + TabId.ToString("N");

    internal string DiskBannerKey => "disk:" + TabId.ToString("N");

    /// <summary>The session record of this tab.</summary>
    internal SessionTab ToSessionTab(int order, bool isActive)
    {
        var tab = IsManaged
            ? SessionTab.ForNote(NodeId!.Value, Title, TabId)
            : SessionTab.ForFile(FilePath!, Title, TabId);
        return tab with
        {
            IsPinned = IsPinned,
            Order = order,
            IsActive = isActive,
            CaretOffset = CaretOffset,
            SelectionStart = SelectionStart,
            SelectionLength = SelectionLength,
            VerticalScrollOffset = VerticalOffset,
            HorizontalScrollOffset = HorizontalOffset,
        };
    }

    /// <summary>Restores the view state from a saved session tab.</summary>
    internal void RestoreViewState(SessionTab saved)
    {
        CaretOffset = Math.Max(0, saved.CaretOffset);
        SelectionStart = Math.Max(0, saved.SelectionStart);
        SelectionLength = Math.Max(0, saved.SelectionLength);
        VerticalOffset = double.IsFinite(saved.VerticalScrollOffset) ? Math.Max(0, saved.VerticalScrollOffset) : 0;
        HorizontalOffset = double.IsFinite(saved.HorizontalScrollOffset) ? Math.Max(0, saved.HorizontalScrollOffset) : 0;
    }

    /// <summary>Stops timers and pending work (the tab closed).</summary>
    internal void OnClosed()
    {
        _loadCancellation?.Cancel();
        _recoveryWriter.Dispose();
        StopPendingTimer();
        StopPlaceholderTimer();
        _context.Banners.Remove(SaveBannerKey);
        _context.Banners.Remove(DiskBannerKey);
    }

    private void StartPlaceholderTimer(long generation)
    {
        StopPlaceholderTimer();
        IsLoadingPlaceholderVisible = false;
        IsLoadingSpinnerVisible = false;
        _placeholderTimer = _context.Clock.CreateTimer(
            _ => _context.Dispatcher.Post(() =>
            {
                if (generation != _loadGeneration || LoadState != TabLoadState.Loading)
                {
                    return;
                }

                if (!IsLoadingPlaceholderVisible)
                {
                    IsLoadingPlaceholderVisible = true;
                }
                else
                {
                    IsLoadingSpinnerVisible = true;
                }
            }),
            null,
            LoadingPlaceholderDelay,
            LoadingSpinnerDelay - LoadingPlaceholderDelay);
    }

    private void StopPlaceholderTimer()
    {
        _placeholderTimer?.Dispose();
        _placeholderTimer = null;
        IsLoadingPlaceholderVisible = false;
        IsLoadingSpinnerVisible = false;
    }

    private void Track(Task task, string description) => _context.Work.Track(task, description);

    private string DescribeLoadError(Exception error) => error switch
    {
        FileNotFoundException or DirectoryNotFoundException => $"{DisplayText.Quote(Title)} no longer exists.",
        ExternalFileTooLargeException tooLarge =>
            $"{DisplayText.Quote(Title)} is {DisplayText.FileSize(tooLarge.Length, _context.Culture)}. NimPad opens files up to {DisplayText.FileSize(tooLarge.Limit, _context.Culture)}.",
        UnauthorizedAccessException => $"NimPad isn't allowed to open {DisplayText.Quote(Title)}.",
        NodeNotFoundException => $"{DisplayText.Quote(Title)} no longer exists.",
        _ => $"{DisplayText.Quote(Title)} couldn't be opened. {error.Message}",
    };

    // Adapts the editor buffer to Core's autosave source. The offset keeps versions growing when a tab's document is
    // replaced by a new one.
    private sealed class BufferSource(IEditorBuffer buffer, long offset) : IAutosaveSource
    {
        public long Version => buffer.Version + offset;

        public string GetSnapshot() => buffer.GetSnapshotText();
    }

    private sealed class UiProgress(TabViewModel tab, long generation) : IProgress<double>
    {
        public void Report(double value) => tab._context.Dispatcher.Run(() =>
        {
            if (generation == tab._loadGeneration)
            {
                tab.LoadProgress = Math.Clamp(value, 0, 1);
            }
        });
    }
}
