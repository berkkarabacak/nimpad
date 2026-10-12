using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NimPad.Presentation.Feedback;

/// <summary>The look of a banner (matches the App's <c>BannerKind</c>).</summary>
public enum BannerKind
{
    /// <summary>Information, such as recovered changes.</summary>
    Info,

    /// <summary>Something needs attention, such as a file changed on disk.</summary>
    Warning,

    /// <summary>Something failed, such as a note that cannot be saved.</summary>
    Error,
}

/// <summary>
/// One banner under the tab strip (design pages 13 and 25): a message, at most one action and an optional dismiss
/// button. Banners stay until their cause clears (<see cref="BannerHostViewModel.Remove"/>) or the user dismisses them.
/// </summary>
public sealed partial class BannerViewModel : ObservableObject
{
    private readonly BannerHostViewModel _host;
    private readonly Func<Task>? _action;
    private readonly bool _removeOnAction;

    internal BannerViewModel(BannerHostViewModel host, string key, BannerKind kind, string message, string? actionText, Func<Task>? action, bool canDismiss, bool removeOnAction)
    {
        _host = host;
        Key = key;
        Kind = kind;
        Message = message;
        ActionText = action is null ? null : actionText;
        _action = action;
        CanDismiss = canDismiss;
        _removeOnAction = removeOnAction;
        ActionCommand = new AsyncRelayCommand(RunActionAsync, () => _action is not null);
        DismissCommand = new RelayCommand(() => _host.Remove(Key), () => CanDismiss);
    }

    /// <summary>Identifies the banner's cause; showing another banner with the same key replaces it.</summary>
    public string Key { get; }

    /// <summary>The look.</summary>
    public BannerKind Kind { get; }

    /// <summary>The message, for example <c>“notes.txt” changed on disk.</c>.</summary>
    public string Message { get; }

    /// <summary>The action label (for example <c>Reload</c>), or <see langword="null"/> for none.</summary>
    public string? ActionText { get; }

    /// <summary>True when the banner has an action.</summary>
    public bool HasAction => _action is not null;

    /// <summary>Runs the action.</summary>
    public IAsyncRelayCommand ActionCommand { get; }

    /// <summary>Whether the banner shows a dismiss button.</summary>
    public bool CanDismiss { get; }

    /// <summary>Dismisses the banner.</summary>
    public IRelayCommand DismissCommand { get; }

    private async Task RunActionAsync()
    {
        if (_action is null)
        {
            return;
        }

        try
        {
            await _action();
            if (_removeOnAction)
            {
                _host.Remove(this);
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"A banner action failed: {ex}");
            _host.ShowFailure(Key, ex.Message);
        }
    }
}

/// <summary>The banners under the tab strip, oldest first.</summary>
public sealed class BannerHostViewModel
{
    /// <summary>The visible banners.</summary>
    public ObservableCollection<BannerViewModel> Banners { get; } = [];

    /// <summary>Raised when a banner action fails; the shell shows an error toast.</summary>
    internal event EventHandler<string>? ActionFailed;

    /// <summary>Shows a banner, replacing (in place) any banner with the same <paramref name="key"/>.</summary>
    /// <param name="key">The banner's cause.</param>
    /// <param name="kind">The look.</param>
    /// <param name="message">The message.</param>
    /// <param name="actionText">The action label, or <see langword="null"/>.</param>
    /// <param name="action">What the action does.</param>
    /// <param name="canDismiss">Whether the banner has a dismiss button.</param>
    /// <param name="removeOnAction">Whether a successful action removes the banner.</param>
    /// <returns>The banner.</returns>
    public BannerViewModel Show(
        string key,
        BannerKind kind,
        string message,
        string? actionText = null,
        Func<Task>? action = null,
        bool canDismiss = true,
        bool removeOnAction = true)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(message);
        var banner = new BannerViewModel(this, key, kind, message, actionText, action, canDismiss, removeOnAction);
        var index = IndexOf(key);
        if (index >= 0)
        {
            Banners[index] = banner;
        }
        else
        {
            Banners.Add(banner);
        }

        return banner;
    }

    /// <summary>Removes the banner with <paramref name="key"/> (its cause cleared).</summary>
    /// <returns><see langword="true"/> when there was one.</returns>
    public bool Remove(string key)
    {
        var index = IndexOf(key);
        if (index < 0)
        {
            return false;
        }

        Banners.RemoveAt(index);
        return true;
    }

    /// <summary>True when a banner with <paramref name="key"/> is shown.</summary>
    public bool Contains(string key) => IndexOf(key) >= 0;

    /// <summary>The banner with <paramref name="key"/>, or <see langword="null"/>.</summary>
    public BannerViewModel? Find(string key) => IndexOf(key) is var index and >= 0 ? Banners[index] : null;

    internal void Remove(BannerViewModel banner) => Banners.Remove(banner);

    internal void ShowFailure(string key, string message) => ActionFailed?.Invoke(this, message);

    private int IndexOf(string key)
    {
        for (var i = 0; i < Banners.Count; i++)
        {
            if (Banners[i].Key == key)
            {
                return i;
            }
        }

        return -1;
    }
}
