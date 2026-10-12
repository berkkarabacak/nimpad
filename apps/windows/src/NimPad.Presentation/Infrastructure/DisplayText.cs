using System.Globalization;

namespace NimPad.Presentation.Infrastructure;

/// <summary>Formatting of the short texts the design shows (timestamps, counts, sizes, quoted names).</summary>
public static class DisplayText
{
    /// <summary>
    /// The sidebar timestamp in the user's local time: the time for today (<c>2:14 PM</c>), the weekday for the six days
    /// before (<c>Sat</c>), the month and day for older dates this year (<c>Oct 3</c>), with the year for earlier years
    /// (<c>Oct 3, 2025</c>).
    /// </summary>
    /// <param name="when">The moment to show.</param>
    /// <param name="now">The current time.</param>
    /// <param name="zone">The user's time zone (<see cref="TimeProvider.LocalTimeZone"/>).</param>
    /// <param name="culture">The formatting culture.</param>
    public static string RelativeTimestamp(DateTimeOffset when, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(culture);
        var local = TimeZoneInfo.ConvertTime(when, zone);
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var days = (localNow.Date - local.Date).TotalDays;
        if (days <= 0)
        {
            return days == 0 ? local.ToString("t", culture) : local.ToString("MMM d", culture);
        }

        if (days < 7)
        {
            return local.ToString("ddd", culture);
        }

        return local.Year == localNow.Year ? local.ToString("MMM d", culture) : local.ToString("MMM d, yyyy", culture);
    }

    /// <summary>A name in the design's curly quotes: <c>“Meeting notes”</c>.</summary>
    public static string Quote(string text) => "“" + text + "”";

    /// <summary><c>1 character</c>, <c>312 characters</c>, <c>12,345 characters</c>.</summary>
    public static string Count(long count, string singular, string plural, CultureInfo culture) =>
        count.ToString("N0", culture) + " " + (count == 1 ? singular : plural);

    /// <summary>A file size for the loading screen: <c>812 bytes</c>, <c>4.2 KB</c>, <c>12.4 MB</c>.</summary>
    public static string FileSize(long bytes, CultureInfo culture)
    {
        const double Kilo = 1024;
        if (bytes < Kilo)
        {
            return bytes.ToString("N0", culture) + " bytes";
        }

        if (bytes < Kilo * Kilo)
        {
            return (bytes / Kilo).ToString("0.#", culture) + " KB";
        }

        if (bytes < Kilo * Kilo * Kilo)
        {
            return (bytes / (Kilo * Kilo)).ToString("0.#", culture) + " MB";
        }

        return (bytes / (Kilo * Kilo * Kilo)).ToString("0.#", culture) + " GB";
    }
}
