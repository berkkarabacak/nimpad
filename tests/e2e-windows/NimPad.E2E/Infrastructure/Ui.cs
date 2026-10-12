using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Exceptions;
using FlaUI.Core.Tools;

namespace NimPad.E2E.Infrastructure;

/// <summary>
/// Timeouts and polling helpers. Every wait polls the UI through FlaUI's <see cref="Retry"/> with a generous timeout
/// instead of sleeping, and a timeout reports what it was waiting for and the last value it saw.
/// </summary>
internal static class Ui
{
    /// <summary>The skip reason for every test: they drive a real Windows desktop.</summary>
    public const string WindowsOnly = "UI automation runs only on Windows (GitHub Actions windows-latest).";

    /// <summary>The default wait for anything the UI should do in response to an action.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>The wait for the process to show its main window and finish starting (cold start on CI).</summary>
    public static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The wait for the process to exit after a graceful close (flushes, checkpoints the database).</summary>
    public static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Polls <paramref name="probe"/> until it returns an object; throws <see cref="TimeoutException"/> otherwise.</summary>
    public static T WaitFor<T>(Func<T?> probe, string description, TimeSpan? timeout = null)
        where T : class
    {
        var limit = timeout ?? Timeout;
        var result = Retry.WhileNull(
            () => Probe(probe),
            limit,
            PollInterval,
            throwOnTimeout: false,
            ignoreException: false);
        return result.Result ?? throw new TimeoutException($"Timed out after {Seconds(limit)} waiting for {description}.");
    }

    /// <summary>Polls <paramref name="condition"/> until it is true; throws <see cref="TimeoutException"/> otherwise.</summary>
    public static void WaitUntil(Func<bool> condition, string description, TimeSpan? timeout = null)
    {
        var limit = timeout ?? Timeout;
        if (!TryWaitUntil(condition, limit))
        {
            throw new TimeoutException($"Timed out after {Seconds(limit)} waiting for {description}.");
        }
    }

    /// <summary>Polls <paramref name="condition"/> until it is true; false when <paramref name="timeout"/> passes first.</summary>
    public static bool TryWaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var result = Retry.WhileFalse(
            () => Probe(condition),
            timeout,
            PollInterval,
            throwOnTimeout: false,
            ignoreException: false);
        return !result.TimedOut && result.Result;
    }

    /// <summary>
    /// Polls <paramref name="read"/> until <paramref name="accept"/> takes its value; the timeout message includes the
    /// last value read, which is usually all it takes to see what went wrong.
    /// </summary>
    public static T WaitForValue<T>(Func<T> read, Func<T, bool> accept, string description, TimeSpan? timeout = null)
    {
        var limit = timeout ?? Timeout;
        var last = default(T);
        var seen = false;
        var ok = TryWaitUntil(
            () =>
            {
                last = read();
                seen = true;
                return accept(last);
            },
            limit);
        if (!ok)
        {
            var lastText = seen ? Describe(last) : "<never read: the element was not available>";
            throw new TimeoutException($"Timed out after {Seconds(limit)} waiting for {description}. Last value: {lastText}.");
        }

        return last!;
    }

    /// <summary>
    /// True for the exceptions UI Automation raises while the UI is changing (an element went away, a property is not
    /// available yet, the provider is busy). Polls treat them as "not yet".
    /// </summary>
    public static bool IsTransient(Exception exception) =>
        exception is FlaUIException or COMException or TimeoutException or UnauthorizedAccessException;

    /// <summary>True when the element is on screen with a non-empty size.</summary>
    public static bool IsShown(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            var bounds = element.Properties.BoundingRectangle.ValueOrDefault;
            return !element.Properties.IsOffscreen.ValueOrDefault && bounds.Width > 0 && bounds.Height > 0;
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            return false;
        }
    }

    /// <summary>The element's Name, or <see langword="null"/> when it is not available.</summary>
    public static string? NameOf(AutomationElement? element) => Read(element, e => e.Properties.Name.ValueOrDefault);

    /// <summary>The element's AutomationId, or <see langword="null"/> when it is not available.</summary>
    public static string? IdOf(AutomationElement? element) => Read(element, e => e.Properties.AutomationId.ValueOrDefault);

    /// <summary>The element's ItemStatus, or <see langword="null"/> when it is not available.</summary>
    public static string? StatusOf(AutomationElement? element) => Read(element, e => e.Properties.ItemStatus.ValueOrDefault);

    /// <summary>The element's Value pattern value, or <see langword="null"/> when it has none.</summary>
    public static string? ValueOf(AutomationElement? element) =>
        Read(element, e => e.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault);

    /// <summary>Line endings normalized to LF, for comparing note text that went through storage (canonical LF).</summary>
    public static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>A value for a failure message, with control characters made visible.</summary>
    public static string Describe<T>(T value) => value switch
    {
        null => "<null>",
        string text => "\"" + text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "\"",
        IEnumerable<string> items => "[" + string.Join(", ", items.Select(Describe)) + "]",
        _ => value.ToString() ?? "<null>",
    };

    private static string? Read(AutomationElement? element, Func<AutomationElement, string?> read)
    {
        if (element is null)
        {
            return null;
        }

        try
        {
            return read(element);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            return null;
        }
    }

    private static T? Probe<T>(Func<T?> probe)
        where T : class
    {
        try
        {
            return probe();
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            return null;
        }
    }

    private static bool Probe(Func<bool> condition)
    {
        try
        {
            return condition();
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            return false;
        }
    }

    private static string Seconds(TimeSpan timeout) => $"{timeout.TotalSeconds:0.#} s";
}
