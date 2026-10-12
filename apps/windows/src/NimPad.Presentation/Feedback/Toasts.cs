using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NimPad.Presentation.Platform;

namespace NimPad.Presentation.Feedback;

/// <summary>The look of a toast.</summary>
public enum ToastKind
{
    /// <summary>A plain message, such as <c>Closed “notes.md”</c> with Undo.</summary>
    Neutral,

    /// <summary>Something finished well (check icon).</summary>
    Success,

    /// <summary>Something failed (error icon).</summary>
    Error,
}

/// <summary>
/// One toast (design pages 10 and 25): a message with at most one action and its shortcut. Toasts never take focus;
/// the action shows its own shortcut. It dismisses itself after <see cref="ToastHostViewModel.Lifetime"/> unless
/// <see cref="Pause"/>d (while the pointer is over it or it has keyboard focus).
/// </summary>
public sealed partial class ToastViewModel : ObservableObject
{
    private readonly ToastHostViewModel _host;
    private readonly Func<Task>? _action;
    private ITimer? _timer;
    private DateTimeOffset _dueAt;
    private TimeSpan _remaining;
    private long _generation;

    internal ToastViewModel(ToastHostViewModel host, string message, ToastKind kind, string? actionText, string? actionShortcutText, Func<Task>? action, string? groupKey)
    {
        _host = host;
        Message = message;
        Kind = kind;
        ActionText = actionText;
        ActionShortcutText = actionShortcutText;
        _action = action;
        GroupKey = groupKey;
        ActionCommand = new AsyncRelayCommand(RunActionAsync, () => _action is not null && !IsDismissed);
        DismissCommand = new RelayCommand(Dismiss);
    }

    /// <summary>The message, for example <c>Closed “packing-list.md”</c>.</summary>
    public string Message { get; }

    /// <summary>The look.</summary>
    public ToastKind Kind { get; }

    /// <summary>The action's label (for example <c>Undo</c>), or <see langword="null"/> for none.</summary>
    public string? ActionText { get; }

    /// <summary>The action's shortcut as shown (for example <c>Ctrl+Shift+T</c>), or <see langword="null"/>.</summary>
    public string? ActionShortcutText { get; }

    /// <summary>True when the toast has an action.</summary>
    public bool HasAction => _action is not null;

    /// <summary>Runs the action and dismisses the toast.</summary>
    public IAsyncRelayCommand ActionCommand { get; }

    /// <summary>Dismisses the toast.</summary>
    public IRelayCommand DismissCommand { get; }

    /// <summary>Toasts with the same key replace each other (for example successive "Closed …" toasts).</summary>
    public string? GroupKey { get; }

    /// <summary>True while the auto-dismiss countdown is paused.</summary>
    [ObservableProperty]
    public partial bool IsPaused { get; private set; }

    /// <summary>True once the toast has left the host.</summary>
    [ObservableProperty]
    public partial bool IsDismissed { get; private set; }

    /// <summary>Stops the countdown (pointer over the toast, or keyboard focus inside it).</summary>
    public void Pause()
    {
        if (IsPaused || IsDismissed)
        {
            return;
        }

        IsPaused = true;
        var remaining = _dueAt - _host.Clock.GetUtcNow();
        _remaining = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        StopTimer();
    }

    /// <summary>
    /// Restarts the countdown with the time that was left when it was paused (at least
    /// <see cref="ToastHostViewModel.MinimumTimeAfterResume"/>).
    /// </summary>
    public void Resume()
    {
        if (!IsPaused || IsDismissed)
        {
            return;
        }

        IsPaused = false;
        StartTimer(_remaining < ToastHostViewModel.MinimumTimeAfterResume ? ToastHostViewModel.MinimumTimeAfterResume : _remaining);
    }

    /// <summary>Removes the toast from the host.</summary>
    public void Dismiss() => _host.Dismiss(this);

    internal void StartTimer(TimeSpan lifetime)
    {
        StopTimer();
        var generation = ++_generation;
        _dueAt = _host.Clock.GetUtcNow() + lifetime;
        _timer = _host.Clock.CreateTimer(
            _ => _host.Dispatcher.Post(() =>
            {
                if (generation == _generation && !IsPaused)
                {
                    Dismiss();
                }
            }),
            null,
            lifetime,
            Timeout.InfiniteTimeSpan);
    }

    internal void OnDismissed()
    {
        StopTimer();
        IsDismissed = true;
        ActionCommand.NotifyCanExecuteChanged();
    }

    private void StopTimer()
    {
        _generation++;
        _timer?.Dispose();
        _timer = null;
    }

    private async Task RunActionAsync()
    {
        if (_action is null || IsDismissed)
        {
            return;
        }

        Dismiss();
        try
        {
            await _action();
        }
        catch (Exception ex)
        {
            Trace.TraceError($"A toast action failed: {ex}");
            _host.ShowError(ex.Message);
        }
    }
}

/// <summary>
/// The toast stack at the bottom of the window: at most <see cref="MaxVisible"/> toasts, newest last; each one
/// dismisses itself after <see cref="Lifetime"/>.
/// </summary>
public sealed class ToastHostViewModel
{
    /// <summary>How many toasts are visible at once; showing another dismisses the oldest.</summary>
    public const int MaxVisible = 3;

    /// <summary>How long a toast stays when nobody interacts with it.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(6);

    /// <summary>The least time a toast stays after <see cref="ToastViewModel.Resume"/>.</summary>
    public static readonly TimeSpan MinimumTimeAfterResume = TimeSpan.FromSeconds(1);

    /// <summary>Creates the host.</summary>
    /// <param name="dispatcher">The UI thread.</param>
    /// <param name="clock">Drives the auto-dismiss timers.</param>
    public ToastHostViewModel(IUiDispatcher dispatcher, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(clock);
        Dispatcher = dispatcher;
        Clock = clock;
    }

    /// <summary>The visible toasts, oldest first.</summary>
    public ObservableCollection<ToastViewModel> Toasts { get; } = [];

    internal IUiDispatcher Dispatcher { get; }

    internal TimeProvider Clock { get; }

    /// <summary>Shows a toast.</summary>
    /// <param name="message">The message.</param>
    /// <param name="kind">The look.</param>
    /// <param name="actionText">The action label (for example <c>Undo</c>), or <see langword="null"/>.</param>
    /// <param name="actionShortcutText">The action's shortcut as shown, or <see langword="null"/>.</param>
    /// <param name="action">What the action does; the toast is dismissed first.</param>
    /// <param name="groupKey">Replaces visible toasts with the same key.</param>
    /// <returns>The toast.</returns>
    public ToastViewModel Show(
        string message,
        ToastKind kind = ToastKind.Neutral,
        string? actionText = null,
        string? actionShortcutText = null,
        Func<Task>? action = null,
        string? groupKey = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (groupKey is not null)
        {
            DismissGroup(groupKey);
        }

        var toast = new ToastViewModel(this, message, kind, action is null ? null : actionText, action is null ? null : actionShortcutText, action, groupKey);
        Toasts.Add(toast);
        while (Toasts.Count > MaxVisible)
        {
            Dismiss(Toasts[0]);
        }

        toast.StartTimer(Lifetime);
        return toast;
    }

    /// <summary>Shows an error toast with no action.</summary>
    public ToastViewModel ShowError(string message) => Show(message, ToastKind.Error);

    /// <summary>Removes a toast (no effect when it is already gone).</summary>
    public void Dismiss(ToastViewModel toast)
    {
        ArgumentNullException.ThrowIfNull(toast);
        if (Toasts.Remove(toast))
        {
            toast.OnDismissed();
        }
    }

    /// <summary>Removes the toasts of a group (their action no longer applies).</summary>
    public void DismissGroup(string groupKey)
    {
        ArgumentNullException.ThrowIfNull(groupKey);
        foreach (var toast in Toasts.Where(toast => toast.GroupKey == groupKey).ToList())
        {
            Dismiss(toast);
        }
    }

    /// <summary>Removes every toast.</summary>
    public void DismissAll()
    {
        foreach (var toast in Toasts.ToList())
        {
            Dismiss(toast);
        }
    }
}
