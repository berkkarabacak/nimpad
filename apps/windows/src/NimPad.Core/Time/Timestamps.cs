using System.Globalization;

namespace NimPad.Core.Time;

/// <summary>
/// The local timestamp format (architecture.md, "Timestamps"): UTC, ISO-8601 with milliseconds and a <c>Z</c>
/// suffix, e.g. <c>2026-10-12T13:59:43.123Z</c>. Instants are truncated to whole milliseconds, which is also what
/// JavaScript's <c>Date#toISOString</c> does.
/// </summary>
public static class Timestamps
{
    /// <summary>The exact custom format string.</summary>
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>The current UTC instant from <paramref name="timeProvider"/>, truncated to milliseconds.</summary>
    public static DateTimeOffset Now(TimeProvider timeProvider) => Truncate(timeProvider.GetUtcNow());

    /// <summary>Converts to UTC and drops sub-millisecond precision.</summary>
    public static DateTimeOffset Truncate(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }

    /// <summary>Formats an instant as <c>yyyy-MM-ddTHH:mm:ss.fffZ</c> (UTC).</summary>
    public static string ToText(DateTimeOffset value) =>
        value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture);

    /// <summary>Parses the exact local timestamp format.</summary>
    /// <exception cref="FormatException">The text is not in the local timestamp format.</exception>
    public static DateTimeOffset Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryParse(text, out var value)
            ? value
            : throw new FormatException($"'{text}' is not a timestamp like 2026-10-12T13:59:43.123Z.");
    }

    /// <summary>Tries to parse the exact local timestamp format.</summary>
    public static bool TryParse(string? text, out DateTimeOffset value) =>
        DateTimeOffset.TryParseExact(
            text,
            Format,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out value);
}
