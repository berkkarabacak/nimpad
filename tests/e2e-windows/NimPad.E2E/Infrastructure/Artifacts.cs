using System.Globalization;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;

namespace NimPad.E2E.Infrastructure;

/// <summary>
/// Failure evidence for CI: a screenshot of the whole desktop, a UI Automation tree dump of every window of each
/// running app, the exception and the app's own logs, under <c>NIMPAD_E2E_ARTIFACTS/&lt;test&gt;-&lt;time&gt;/</c>.
/// Capturing never throws: it must not hide the real failure.
/// </summary>
internal static class Artifacts
{
    /// <summary>Environment variable with the folder for failure artifacts (set by CI, uploaded on failure).</summary>
    public const string DirectoryVariable = "NIMPAD_E2E_ARTIFACTS";

    private const int MaxTreeDepth = 60;
    private const int MaxTreeNodes = 6000;

    /// <summary>The artifacts folder: <c>NIMPAD_E2E_ARTIFACTS</c>, or <c>e2e-artifacts</c> next to the test binaries.</summary>
    public static string RootDirectory
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable(DirectoryVariable);
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(AppContext.BaseDirectory, "e2e-artifacts")
                : Path.GetFullPath(configured.Trim().Trim('"'));
        }
    }

    /// <summary>Writes everything that helps explain <paramref name="failure"/>; returns the folder, or null if nothing could be written.</summary>
    public static string? CaptureFailure(
        string testName,
        Exception failure,
        IReadOnlyList<AppSession> sessions,
        IReadOnlyList<string> dataDirectories)
    {
        string folder;
        try
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            folder = Path.Combine(RootDirectory, $"{testName}-{stamp}");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "error.txt"), failure.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        Attempt(folder, "screen", () =>
        {
            using var image = Capture.Screen();
            image.ToFile(Path.Combine(folder, "screen.png"));
        });

        foreach (var session in sessions.Where(session => !session.HasExited))
        {
            Attempt(folder, $"session{session.Number}", () => DumpSession(session, folder));
        }

        for (var i = 0; i < dataDirectories.Count; i++)
        {
            var logs = Path.Combine(dataDirectories[i], "logs");
            var target = Path.Combine(folder, $"data{i + 1}-logs");
            Attempt(folder, $"data{i + 1}-logs", () => CopyLogs(logs, target));
        }

        return folder;
    }

    /// <summary>One line describing an element: control type, AutomationId, Name, ItemStatus, class, bounds and state.</summary>
    public static string Describe(AutomationElement element)
    {
        var properties = element.Properties;
        var builder = new StringBuilder();
        builder.Append(Read(() => properties.ControlType.ValueOrDefault.ToString()));
        Append(builder, "id", Read(() => properties.AutomationId.ValueOrDefault));
        Append(builder, "name", Read(() => properties.Name.ValueOrDefault));
        Append(builder, "status", Read(() => properties.ItemStatus.ValueOrDefault));
        Append(builder, "class", Read(() => properties.ClassName.ValueOrDefault));
        Append(builder, "bounds", Read(() => properties.BoundingRectangle.ValueOrDefault.ToString()));
        if (Read(() => properties.IsOffscreen.ValueOrDefault.ToString()) == bool.TrueString)
        {
            builder.Append(" offscreen");
        }

        if (Read(() => properties.IsEnabled.ValueOrDefault.ToString()) == bool.FalseString)
        {
            builder.Append(" disabled");
        }

        if (Read(() => properties.HasKeyboardFocus.ValueOrDefault.ToString()) == bool.TrueString)
        {
            builder.Append(" FOCUSED");
        }

        return builder.ToString();
    }

    private static void DumpSession(AppSession session, string folder)
    {
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"NimPad pid {session.ProcessId}, data directory {session.DataDirectory}");
        var focused = Read(() => session.Automation.FocusedElement() is { } element ? Describe(element) : "<none>");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Focused element: {focused}");
        builder.AppendLine();
        var budget = new int[] { MaxTreeNodes };
        foreach (var window in session.FindTopLevelElements())
        {
            DumpTree(window, builder, 0, budget);
            builder.AppendLine();
        }

        File.WriteAllText(Path.Combine(folder, $"session{session.Number}-uia-tree.txt"), builder.ToString());
    }

    private static void DumpTree(AutomationElement element, StringBuilder builder, int depth, int[] budget)
    {
        if (budget[0]-- <= 0)
        {
            builder.Append(' ', depth * 2).AppendLine("… (node limit reached)");
            return;
        }

        builder.Append(' ', depth * 2).AppendLine(Describe(element));
        if (depth >= MaxTreeDepth)
        {
            return;
        }

        AutomationElement[] children;
        try
        {
            children = element.FindAllChildren();
        }
        catch (Exception ex) when (Ui.IsTransient(ex))
        {
            builder.Append(' ', (depth + 1) * 2).AppendLine(CultureInfo.InvariantCulture, $"<children unavailable: {ex.GetType().Name}>");
            return;
        }

        foreach (var child in children)
        {
            DumpTree(child, builder, depth + 1, budget);
        }
    }

    private static void CopyLogs(string source, string target)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            // The app may still hold the log open; read it shared instead of File.Copy.
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var output = File.Create(Path.Combine(target, Path.GetFileName(file)));
            input.CopyTo(output);
        }
    }

    private static void Attempt(string folder, string what, Action capture)
    {
        try
        {
            capture();
        }
        catch (Exception ex)
        {
            // Never let evidence collection replace the real failure; note why this piece is missing instead.
            try
            {
                File.AppendAllText(Path.Combine(folder, "capture-errors.txt"), $"{what}: {ex}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
        }
    }

    private static void Append(StringBuilder builder, string label, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            builder.Append(' ').Append(label).Append("=\"").Append(value).Append('"');
        }
    }

    private static string? Read(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (Ui.IsTransient(ex))
        {
            return null;
        }
    }
}
