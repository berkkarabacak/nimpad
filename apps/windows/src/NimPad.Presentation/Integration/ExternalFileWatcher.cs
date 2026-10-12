using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using NimPad.Presentation.Platform;
using NimPad.Presentation.Shell;
using NimPad.Presentation.Tabs;

namespace NimPad.Presentation.Integration;

/// <summary>
/// Watches the files of the open external tabs and tells their tabs when another program changes, replaces, moves
/// or deletes them (<see cref="TabViewModel.OnFileChangedOnDisk"/>, <see cref="TabViewModel.OnFileMissingOnDisk"/>).
/// </summary>
/// <remarks>
/// <para>One <see cref="FileSystemWatcher"/> per external tab watches the file's folder, filtered by the file name.
/// Events are debounced (<see cref="DebounceDelay"/> after the last one); then the file is read once off the UI thread
/// and <see cref="ExternalFileChanges.Evaluate"/> decides, on the UI thread, what happened. A file that still carries
/// the write time of NimPad's own save (<see cref="TabViewModel.LastSavedWriteTimeUtc"/>) is our save and is ignored.
/// Watchers follow Save as, stop when their tab closes and are all released by <see cref="Dispose"/>.</para>
/// <para>Create it on the UI thread after <see cref="ShellViewModel.InitializeAsync"/>. Watcher failures (a folder
/// that is gone, a share without change notifications) are logged; they never reach the user.</para>
/// </remarks>
public sealed class ExternalFileWatcher : IDisposable
{
    /// <summary>How long the events of one file must be quiet before the file is checked.</summary>
    public static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(300);

    private readonly ShellViewModel _shell;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _clock;
    private readonly Dictionary<TabViewModel, Entry> _entries = [];
    private bool _disposed;

    /// <summary>Starts watching the shell's external tabs (call on the UI thread).</summary>
    /// <param name="shell">The initialized shell.</param>
    /// <param name="clock">The debounce clock; the shell's clock by default.</param>
    public ExternalFileWatcher(ShellViewModel shell, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(shell);
        _shell = shell;
        _dispatcher = shell.Context.Dispatcher;
        _clock = clock ?? shell.Context.Clock;
        if (!_dispatcher.CheckAccess())
        {
            throw new InvalidOperationException("Create the file watcher on the UI thread.");
        }

        shell.Tabs.Tabs.CollectionChanged += OnTabsChanged;
        Synchronize();
    }

    /// <summary>The files watched now (one per external tab).</summary>
    public IReadOnlyList<string> WatchedFiles => [.. _entries.Values.Select(entry => entry.Path)];

    /// <summary>Stops watching and releases every watcher.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shell.Tabs.Tabs.CollectionChanged -= OnTabsChanged;
        foreach (var tab in _entries.Keys.ToList())
        {
            Remove(tab);
        }
    }

    /// <summary>True while a check of <paramref name="tab"/>'s file waits for its events to settle (tests).</summary>
    internal bool HasPendingCheck(TabViewModel tab) => _entries.TryGetValue(tab, out var entry) && entry.IsPending;

    /// <summary>Whether a real <see cref="FileSystemWatcher"/> runs for <paramref name="tab"/> (tests).</summary>
    internal bool IsWatching(TabViewModel tab) => _entries.TryGetValue(tab, out var entry) && entry.IsWatching;

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Synchronize();

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.FilePath) && sender is TabViewModel tab && _entries.ContainsKey(tab))
        {
            // Save as: watch the new file instead.
            Remove(tab);
            Synchronize();
        }
    }

    private void Synchronize()
    {
        if (_disposed)
        {
            return;
        }

        var open = _shell.Tabs.Tabs.Where(tab => tab.IsExternal && tab.FilePath is not null).ToHashSet();
        foreach (var tab in _entries.Keys.Where(tab => !open.Contains(tab)).ToList())
        {
            Remove(tab);
        }

        foreach (var tab in open)
        {
            if (!_entries.ContainsKey(tab))
            {
                var entry = new Entry(this, tab, tab.FilePath!);
                _entries.Add(tab, entry);
                tab.PropertyChanged += OnTabPropertyChanged;
                entry.Start();
            }
        }
    }

    private void Remove(TabViewModel tab)
    {
        if (_entries.Remove(tab, out var entry))
        {
            tab.PropertyChanged -= OnTabPropertyChanged;
            entry.Dispose();
        }
    }

    // On the UI thread, with the file's state read after its events settled.
    private void Apply(Entry entry, ExternalFileState? state)
    {
        var tab = entry.Tab;
        if (_disposed || entry.IsDisposed || _shell.IsShuttingDown || !_entries.TryGetValue(tab, out var current)
            || !ReferenceEquals(current, entry))
        {
            return;
        }

        try
        {
            switch (ExternalFileChanges.Evaluate(state, tab.LastSavedWriteTimeUtc, tab.IsMissingOnDisk))
            {
                case ExternalFileChangeKind.Missing:
                    tab.OnFileMissingOnDisk();
                    break;
                case ExternalFileChangeKind.Changed:
                case ExternalFileChangeKind.Reappeared:
                    tab.OnFileChangedOnDisk(state!.Value.LastWriteTimeUtc);
                    break;
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Handling a change of {entry.Path} failed: {ex}");
        }
    }

    // Recreates a watcher that reported an error (for example its buffer overflowed), on the UI thread.
    private void Restart(Entry entry)
    {
        if (!_disposed && !entry.IsDisposed && _entries.TryGetValue(entry.Tab, out var current) && ReferenceEquals(current, entry))
        {
            entry.Start();
        }
    }

    private sealed class Entry : IDisposable
    {
        private readonly ExternalFileWatcher _owner;
        private readonly ITimer _timer;
        private readonly Lock _gate = new();
        private FileSystemWatcher? _watcher;
        private volatile bool _disposed;
        private volatile bool _pending;

        public Entry(ExternalFileWatcher owner, TabViewModel tab, string path)
        {
            _owner = owner;
            Tab = tab;
            Path = path;
            _timer = owner._clock.CreateTimer(_ => Check(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        public TabViewModel Tab { get; }

        public string Path { get; }

        public bool IsDisposed => _disposed;

        public bool IsPending => _pending;

        public bool IsWatching => _watcher is not null;

        public void Start()
        {
            StopWatcher();
            var directory = System.IO.Path.GetDirectoryName(Path);
            var name = System.IO.Path.GetFileName(Path);
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(name))
            {
                return;
            }

            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(directory, name)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                };
                watcher.Changed += OnEvent;
                watcher.Created += OnEvent;
                watcher.Deleted += OnEvent;
                watcher.Renamed += OnEvent;
                watcher.Error += OnError;
                watcher.EnableRaisingEvents = true;
                lock (_gate)
                {
                    _watcher = watcher;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // The folder is gone or cannot be watched (some network shares); the tab still works without it.
                Trace.TraceWarning($"Cannot watch {Path} for changes: {ex.Message}");
                watcher?.Dispose();
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _pending = false;
            StopWatcher();
            _timer.Dispose();
        }

        private void StopWatcher()
        {
            FileSystemWatcher? watcher;
            lock (_gate)
            {
                watcher = _watcher;
                _watcher = null;
            }

            if (watcher is not null)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Changed -= OnEvent;
                watcher.Created -= OnEvent;
                watcher.Deleted -= OnEvent;
                watcher.Renamed -= OnEvent;
                watcher.Error -= OnError;
                watcher.Dispose();
            }
        }

        // File-system events arrive on thread-pool threads.
        private void OnEvent(object sender, FileSystemEventArgs e) => Schedule();

        private void OnError(object sender, ErrorEventArgs e)
        {
            Trace.TraceWarning($"Watching {Path} failed: {e.GetException().Message}");
            Schedule();
            try
            {
                _owner._dispatcher.Post(() => _owner.Restart(this));
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Cannot restart the watcher of {Path}: {ex.Message}");
            }
        }

        private void Schedule()
        {
            if (_disposed)
            {
                return;
            }

            _pending = true;
            try
            {
                _timer.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // The tab closed meanwhile.
            }
        }

        // The debounce elapsed (thread pool): read the file here, decide on the UI thread.
        private void Check()
        {
            if (_disposed)
            {
                return;
            }

            _pending = false;
            var state = ExternalFileState.Read(Path);
            try
            {
                _owner._dispatcher.Post(() => _owner.Apply(this, state));
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Cannot report the change of {Path}: {ex.Message}");
            }
        }
    }
}
