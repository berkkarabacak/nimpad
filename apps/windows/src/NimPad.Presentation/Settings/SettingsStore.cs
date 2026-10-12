using System.Diagnostics;
using NimPad.Core.Domain;
using NimPad.Core.Services;

namespace NimPad.Presentation.Settings;

/// <summary>
/// The current <see cref="AppSettings"/>, shared by every view model. Changes apply live (<see cref="Changed"/> is
/// raised on the UI thread) and are persisted immediately through <see cref="SettingsService"/>; saves are serialized,
/// so the last change always wins.
/// </summary>
public sealed class SettingsStore
{
    private SettingsService? _service;
    private Task _lastSave = Task.CompletedTask;

    /// <summary>The settings in effect.</summary>
    public AppSettings Current { get; private set; } = AppSettings.Default;

    /// <summary>Raised after <see cref="Current"/> changed (old value, new value).</summary>
    public event EventHandler<SettingsChangedEventArgs>? Changed;

    /// <summary>Raised when persisting the settings failed.</summary>
    public event EventHandler<Exception>? SaveFailed;

    /// <summary>Completes when every change made so far is stored.</summary>
    public Task WhenSavedAsync() => _lastSave;

    /// <summary>Replaces the settings with a change of the current ones, applies it and saves it.</summary>
    /// <param name="change">Derives the new settings from the current ones.</param>
    /// <returns><see langword="true"/> when the settings changed.</returns>
    /// <exception cref="ArgumentException">The new settings are invalid (<see cref="AppSettings.Validate"/>).</exception>
    public bool Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var previous = Current;
        var next = change(previous);
        if (next.Validate() is { Count: > 0 } problems)
        {
            throw new ArgumentException(string.Join(" ", problems), nameof(change));
        }

        if (next == previous)
        {
            return false;
        }

        Current = next;
        Persist(next);
        Changed?.Invoke(this, new SettingsChangedEventArgs(previous, next));
        return true;
    }

    /// <summary>Attaches the storage and loads the saved settings (without raising <see cref="Changed"/> for unchanged values).</summary>
    internal async Task LoadAsync(SettingsService service, CancellationToken cancellationToken = default)
    {
        _service = service;
        var loaded = await service.LoadAsync(cancellationToken);
        var previous = Current;
        Current = loaded;
        if (loaded != previous)
        {
            Changed?.Invoke(this, new SettingsChangedEventArgs(previous, loaded));
        }
    }

    /// <summary>Detaches the storage (shutdown).</summary>
    internal void Detach() => _service = null;

    private void Persist(AppSettings settings)
    {
        if (_service is not { } service)
        {
            return;
        }

        var previous = _lastSave;
        _lastSave = SaveAfterAsync(previous, service, settings);
    }

    private async Task SaveAfterAsync(Task previous, SettingsService service, AppSettings settings)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Reported by that save.
        }

        try
        {
            await service.SaveAsync(settings).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Saving the settings failed: {ex}");
            SaveFailed?.Invoke(this, ex);
        }
    }
}

/// <summary>The settings before and after a change.</summary>
public sealed class SettingsChangedEventArgs(AppSettings oldSettings, AppSettings newSettings) : EventArgs
{
    /// <summary>The settings before the change.</summary>
    public AppSettings OldSettings { get; } = oldSettings;

    /// <summary>The settings now in effect.</summary>
    public AppSettings NewSettings { get; } = newSettings;
}
