using System.Diagnostics;
using System.Globalization;
using NimPad.Core.Domain;
using NimPad.Core.Services;
using NimPad.Presentation.Feedback;
using NimPad.Presentation.Platform;
using NimPad.Presentation.Settings;
using NimPad.Presentation.Workspace;

namespace NimPad.Presentation.Infrastructure;

/// <summary>
/// What the view models share: the platform services, the clock, the Core services (attached once the database is
/// open), the workspace model, the settings, toasts, banners and background work.
/// </summary>
internal sealed class ViewModelContext
{
    private CoreServices? _core;

    /// <summary>Creates the context; Core is attached later (<see cref="Attach"/>).</summary>
    public ViewModelContext(PlatformServices platform, TimeProvider clock, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(clock);
        Platform = platform;
        Clock = clock;
        Culture = culture ?? CultureInfo.CurrentCulture;
        Toasts = new ToastHostViewModel(platform.Dispatcher, clock);
        Model = new WorkspaceModel(platform.Dispatcher);
    }

    /// <summary>The App's platform services.</summary>
    public PlatformServices Platform { get; }

    /// <summary>The UI thread.</summary>
    public IUiDispatcher Dispatcher => Platform.Dispatcher;

    /// <summary>The clock for every timer and timestamp.</summary>
    public TimeProvider Clock { get; }

    /// <summary>The culture for timestamps and counts.</summary>
    public CultureInfo Culture { get; set; }

    /// <summary>Fire-and-forget work in flight.</summary>
    public BackgroundWork Work { get; } = new();

    /// <summary>The toast stack.</summary>
    public ToastHostViewModel Toasts { get; }

    /// <summary>The banners.</summary>
    public BannerHostViewModel Banners { get; } = new();

    /// <summary>The settings in effect.</summary>
    public SettingsStore Settings { get; } = new();

    /// <summary>The in-memory workspace tree.</summary>
    public WorkspaceModel Model { get; }

    /// <summary>True once the Core services are attached.</summary>
    public bool IsReady => _core is not null && Model.Workspace is not null;

    /// <summary>The Core services.</summary>
    /// <exception cref="InvalidOperationException">The shell is not initialized yet (or already shut down).</exception>
    public CoreServices Core => _core ?? throw new InvalidOperationException("NimPad is not initialized yet.");

    /// <summary>The workspace shown.</summary>
    public Core.Domain.Workspace Workspace => Model.Workspace ?? throw new InvalidOperationException("NimPad is not initialized yet.");

    /// <summary>The folder of the last export (the Save dialog starts there next time).</summary>
    public string? LastExportDirectory { get; set; }

    /// <summary>Makes the Core services available (after the database opened).</summary>
    public void Attach(CoreServices core) => _core = core;

    /// <summary>Forgets the Core services (shutdown).</summary>
    public void Detach() => _core = null;

    /// <summary>
    /// Runs a user action; a failure is logged and shown as an error toast instead of escaping (an exception that
    /// reaches WPF's dispatcher would end the app). Cancellation is silent.
    /// </summary>
    public async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            // The user (or a newer action) cancelled it.
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    /// <summary>Runs a user action that returns a value; on failure it is reported and <paramref name="fallback"/> returned.</summary>
    public async Task<T> GuardAsync<T>(Func<Task<T>> action, T fallback)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException)
        {
            return fallback;
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
            return fallback;
        }
    }

    /// <summary>Logs a failure and shows it as an error toast.</summary>
    public void ReportFailure(Exception error)
    {
        Trace.TraceError($"A user action failed: {error}");
        Toasts.ShowError(UserMessage(error));
    }

    /// <summary>The user-facing text of an error (Core's messages are written for people).</summary>
    public static string UserMessage(Exception error) => error switch
    {
        InvalidNodeNameException name => name.Reason,
        NodeOperationException => error.Message,
        IOException or UnauthorizedAccessException => error.Message,
        _ => "Something went wrong: " + error.Message,
    };

    /// <summary>A sidebar timestamp in the user's time zone (<see cref="DisplayText.RelativeTimestamp"/>).</summary>
    public string FormatTimestamp(DateTimeOffset when) =>
        DisplayText.RelativeTimestamp(when, Clock.GetUtcNow(), Clock.LocalTimeZone, Culture);
}
