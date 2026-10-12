using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace NimPad.E2E.Infrastructure;

/// <summary>
/// One running NimPad process driven through UI Automation (FlaUI UIA3). It launches the app with the test flags
/// (<c>--data-dir &lt;dir&gt; --no-single-instance --no-hotkey</c>), finds elements by AutomationId, sends real keyboard
/// input, reads the editor text, tabs, breadcrumbs and status bar, and ends with a graceful close or a kill.
/// Create it through <see cref="Scenario.Launch"/> so a failure captures its screenshot and UI tree.
/// </summary>
internal sealed partial class AppSession : IDisposable
{
    /// <summary>The window size the tests run at (Wide layout). It is clamped to the primary screen.</summary>
    public static readonly Size PreferredWindowSize = new(1280, 800);

    /// <summary>How long a scenario waits after typing before it closes or kills the app (autosave max-wait is 1 s).</summary>
    public static readonly TimeSpan AutosaveGrace = TimeSpan.FromSeconds(1.5);

    // Room left for the taskbar, so the status bar is not covered on a small screen (hosted CI runners are often 1024x768).
    private const int TaskbarAllowance = 48;
    private const int MaxParentDepth = 100;

    private readonly Process _process;
    private Window? _mainWindow;
    private IntPtr _mainWindowHandle;
    private bool _disposed;

    private AppSession(Process process, string dataDirectory, int number)
    {
        _process = process;
        DataDirectory = dataDirectory;
        Number = number;
        Automation = new UIA3Automation();
    }

    /// <summary>The UI Automation client used for this process.</summary>
    public UIA3Automation Automation { get; }

    /// <summary>The <c>--data-dir</c> the app was started with.</summary>
    public string DataDirectory { get; }

    /// <summary>1 for the first launch of a scenario, 2 for the relaunch, …</summary>
    public int Number { get; }

    /// <summary>The process id.</summary>
    public int ProcessId => _process.Id;

    /// <summary>True once the process has ended.</summary>
    public bool HasExited => _process.HasExited;

    /// <summary>The main window (AutomationId <c>MainWindow</c>).</summary>
    public Window MainWindow => _mainWindow ?? throw new InvalidOperationException("The main window has not been found yet.");

    /// <summary>The main window's ItemStatus: <c>theme:light</c> or <c>theme:dark</c>.</summary>
    public string? ThemeMarker => Ui.StatusOf(MainWindow);

    /// <summary>The command line the tests use: the data directory, the two test flags, then the files to open.</summary>
    public static IReadOnlyList<string> BuildArguments(string dataDirectory, IReadOnlyList<string> files) =>
        ["--data-dir", dataDirectory, "--no-single-instance", "--no-hotkey", .. files];

    /// <summary>Starts NimPad. Call <see cref="WaitUntilReady"/> next.</summary>
    public static AppSession Start(string executable, string dataDirectory, IReadOnlyList<string> files, int number)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
        };
        foreach (var argument in BuildArguments(dataDirectory, files))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start '{executable}'.");
        return new AppSession(process, dataDirectory, number);
    }

    /// <summary>
    /// Waits for the main window and for startup to finish (a tab or the "No notes open" state), then sizes the window
    /// to <see cref="PreferredWindowSize"/> at the top-left of the primary screen and brings it to the front.
    /// </summary>
    public void WaitUntilReady()
    {
        _mainWindow = Ui.WaitFor(FindMainWindow, $"the NimPad main window (AutomationId {AutomationIds.MainWindow})", Ui.StartupTimeout);
        _mainWindowHandle = _mainWindow.Properties.NativeWindowHandle.ValueOrDefault;
        Ui.WaitUntil(
            IsStarted,
            $"NimPad to finish starting: a tab ({AutomationIds.TabPrefix}0) or the “No notes open” state ({AutomationIds.EmptyStateNewNote})",
            Ui.StartupTimeout);
        ResizeWindow();
        EnsureForeground();
    }

    // ------------------------------------------------------------------ finding elements

    /// <summary>The first element in the main window with this AutomationId, or <see langword="null"/> (no waiting).</summary>
    public AutomationElement? FindByAutomationId(string automationId)
    {
        ThrowIfExited();
        return MainWindow.FindFirstDescendant(Automation.ConditionFactory.ByAutomationId(automationId));
    }

    /// <summary>Waits for an element in the main window with this AutomationId.</summary>
    public AutomationElement WaitForAutomationId(string automationId, TimeSpan? timeout = null) =>
        Ui.WaitFor(() => FindByAutomationId(automationId), $"the element with AutomationId {automationId}", timeout);

    /// <summary>Waits for an element with this AutomationId that is on screen.</summary>
    public AutomationElement WaitForShown(string automationId, TimeSpan? timeout = null) =>
        Ui.WaitFor(
            () => FindByAutomationId(automationId) is { } element && Ui.IsShown(element) ? element : null,
            $"the element with AutomationId {automationId} to be on screen",
            timeout);

    /// <summary>Waits until no element with this AutomationId is on screen.</summary>
    public void WaitUntilGone(string automationId, TimeSpan? timeout = null) =>
        Ui.WaitUntil(() => !Ui.IsShown(FindByAutomationId(automationId)), $"the element with AutomationId {automationId} to go away", timeout);

    /// <summary>
    /// A window or dialog of this process with this AutomationId: a top-level window, an owned window (UI Automation
    /// lists those under their owner) or an in-window dialog.
    /// </summary>
    public AutomationElement? FindWindow(string automationId)
    {
        ThrowIfExited();
        var conditions = Automation.ConditionFactory;
        var byId = conditions.ByAutomationId(automationId);
        return Automation.GetDesktop().FindFirstChild(conditions.ByProcessId(ProcessId).And(byId))
            ?? MainWindow.FindFirstChild(byId)
            ?? MainWindow.FindFirstDescendant(byId);
    }

    /// <summary>Every top-level window of this process (for diagnostics).</summary>
    public IReadOnlyList<AutomationElement> FindTopLevelElements() =>
        Automation.GetDesktop().FindAllChildren(Automation.ConditionFactory.ByProcessId(ProcessId));

    /// <summary>Waits until the element with this AutomationId has exactly this Name.</summary>
    public string WaitForName(string automationId, string expected, TimeSpan? timeout = null) =>
        Ui.WaitForValue(
            () => Ui.NameOf(FindByAutomationId(automationId)),
            name => name == expected,
            $"{automationId} to read {Ui.Describe(expected)}",
            timeout)!;

    /// <summary>Waits until the main window reports this theme marker (ItemStatus).</summary>
    public void WaitForThemeMarker(string expected) =>
        Ui.WaitForValue(() => ThemeMarker, marker => marker == expected, $"{AutomationIds.MainWindow} ItemStatus to be {Ui.Describe(expected)}");

    // ------------------------------------------------------------------ keyboard

    /// <summary>Presses a shortcut (all keys down in order, then up in reverse), e.g. Ctrl+Shift+T.</summary>
    public void PressShortcut(params VirtualKeyShort[] keys)
    {
        EnsureForeground();
        Keyboard.TypeSimultaneously(keys);
        Wait.UntilInputIsProcessed();
    }

    /// <summary>Presses keys one after another, e.g. six times Right.</summary>
    public void PressKeys(params VirtualKeyShort[] keys)
    {
        EnsureForeground();
        Keyboard.Type(keys);
        Wait.UntilInputIsProcessed();
    }

    /// <summary>
    /// Types text into whatever has keyboard focus. <c>\n</c> presses Enter: FlaUI types characters through VkKeyScan,
    /// which maps a line feed to Ctrl+Enter, so line breaks are sent as the Enter key instead.
    /// </summary>
    public void TypeText(string text)
    {
        EnsureForeground();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                Keyboard.Type(VirtualKeyShort.RETURN);
            }

            if (lines[i].Length > 0)
            {
                Keyboard.Type(lines[i]);
            }
        }

        Wait.UntilInputIsProcessed();
    }

    /// <summary>
    /// Makes sure keyboard input reaches NimPad: something in this process must have keyboard focus. Windows only lets
    /// a background process take the foreground right after input, so if a plain SetForegroundWindow is refused a lone
    /// Alt press lifts the lock first (the workaround pywinauto's set_focus uses); that Alt goes to the window that is
    /// in front, which is not NimPad.
    /// </summary>
    public void EnsureForeground()
    {
        ThrowIfExited();
        if (IsFocusInApp())
        {
            return;
        }

        User32.SetForegroundWindow(_mainWindowHandle);
        if (Ui.TryWaitUntil(IsFocusInApp, TimeSpan.FromSeconds(2)))
        {
            return;
        }

        Keyboard.Type(VirtualKeyShort.ALT);
        User32.SetForegroundWindow(_mainWindowHandle);
        Ui.WaitUntil(IsFocusInApp, "NimPad to come to the foreground with keyboard focus");
    }

    /// <summary>True when the focused element is <paramref name="container"/> or inside it.</summary>
    public bool IsFocusWithin(AutomationElement container)
    {
        var walker = Automation.TreeWalkerFactory.GetRawViewWalker();
        var element = Automation.FocusedElement();
        for (var depth = 0; element is not null && depth < MaxParentDepth; depth++)
        {
            if (Automation.Compare(element, container))
            {
                return true;
            }

            element = walker.GetParent(element);
        }

        return false;
    }

    // ------------------------------------------------------------------ editor

    /// <summary>
    /// Moves keyboard focus to <paramref name="element"/> through UI Automation (SetFocus). WPF reports a failure when
    /// the element hands focus on to a child (AvalonEdit's TextEditor passes it to its TextArea), even though focus
    /// did move, so the outcome is checked by the caller instead of trusting the call.
    /// </summary>
    public static void TryFocus(AutomationElement element)
    {
        try
        {
            element.Focus();
        }
        catch (Exception ex) when (Ui.IsTransient(ex) || ex is InvalidOperationException)
        {
            // Checked by the caller (IsFocusWithin / the element's own state).
        }
    }

    /// <summary>
    /// Gives the editor keyboard focus through UI Automation (TextEditor forwards it to its TextArea), which leaves the
    /// caret where it is. Only if that does not land does it click into the editor like a user would.
    /// </summary>
    public void FocusEditor()
    {
        EnsureForeground();
        var editor = WaitForShown(AutomationIds.Editor);
        TryFocus(editor);
        if (Ui.TryWaitUntil(() => IsFocusWithin(editor), TimeSpan.FromSeconds(3)))
        {
            return;
        }

        editor.Click();
        Ui.WaitUntil(() => IsFocusWithin(editor), $"keyboard focus in the editor ({AutomationIds.Editor})");
    }

    /// <summary>The editor's document text through its Value pattern (AvalonEdit's TextEditorAutomationPeer).</summary>
    public string ReadEditorText() =>
        TryReadEditorText() ?? throw new InvalidOperationException(
            $"The {AutomationIds.Editor} element is missing or does not expose the Value pattern (contract: AvalonEdit's TextEditorAutomationPeer).");

    /// <summary>Waits until the editor shows <paramref name="expected"/>, ignoring line-ending differences (notes are stored with LF).</summary>
    public void WaitForEditorText(string expected) =>
        Ui.WaitForValue(
            TryReadEditorText,
            text => text is not null && Ui.NormalizeNewlines(text) == Ui.NormalizeNewlines(expected),
            $"the editor text to be {Ui.Describe(expected)} (line endings ignored)");

    /// <summary>Waits until the editor shows exactly <paramref name="expected"/>, line endings included.</summary>
    public void WaitForEditorTextExact(string expected) =>
        Ui.WaitForValue(TryReadEditorText, text => text == expected, $"the editor text to be exactly {Ui.Describe(expected)}");

    /// <summary>The names of the breadcrumb segments, in order (consecutive duplicates merged, e.g. a button and its text).</summary>
    public IReadOnlyList<string> ReadBreadcrumbs()
    {
        var names = new List<string>();
        if (FindByAutomationId(AutomationIds.Breadcrumbs) is not { } breadcrumbs)
        {
            return names;
        }

        foreach (var element in breadcrumbs.FindAllDescendants())
        {
            var name = Ui.NameOf(element)?.Trim();
            if (!string.IsNullOrEmpty(name) && (names.Count == 0 || names[^1] != name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>
    /// Waits until the breadcrumbs contain <paramref name="inOrder"/> in this order (other segments such as the
    /// workspace name or separators may sit between them) and never <paramref name="absent"/>.
    /// </summary>
    public IReadOnlyList<string> WaitForBreadcrumbs(IReadOnlyList<string> inOrder, string? absent = null, TimeSpan? timeout = null) =>
        Ui.WaitForValue(
            ReadBreadcrumbs,
            names => ContainsInOrder(names, inOrder) && (absent is null || !names.Contains(absent)),
            $"the breadcrumbs ({AutomationIds.Breadcrumbs}) to contain {Ui.Describe(inOrder)} in order" +
                (absent is null ? string.Empty : $" and not {Ui.Describe(absent)}"),
            timeout);

    // ------------------------------------------------------------------ tabs

    /// <summary>The tab items (<c>Tab_&lt;n&gt;</c>) in visual order, one per index (the outermost if an id appears twice).</summary>
    public IReadOnlyList<AutomationElement> FindTabs()
    {
        var root = FindByAutomationId(AutomationIds.TabStrip) ?? MainWindow;
        return root.FindAllDescendants()
            .Select(element => (Element: element, Index: TabIndexOf(element)))
            .Where(tab => tab.Index >= 0)
            .GroupBy(tab => tab.Index)
            .OrderBy(group => group.Key)
            .Select(group => group.First().Element)
            .ToList();
    }

    /// <summary>The tab titles (their Names) in visual order.</summary>
    public IReadOnlyList<string> ReadTabTitles() => FindTabs().Select(tab => Ui.NameOf(tab) ?? "<unnamed>").ToList();

    /// <summary>The tab with this title, or <see langword="null"/>.</summary>
    public AutomationElement? FindTab(string title) => FindTabs().FirstOrDefault(tab => Ui.NameOf(tab) == title);

    /// <summary>Waits until the tab titles are exactly <paramref name="expected"/>, in order.</summary>
    public IReadOnlyList<string> WaitForTabTitles(IReadOnlyList<string> expected) =>
        Ui.WaitForValue(ReadTabTitles, titles => titles.SequenceEqual(expected), $"the tabs to be {Ui.Describe(expected)}");

    /// <summary>Waits until the tab titles satisfy <paramref name="accept"/>.</summary>
    public IReadOnlyList<string> WaitForTabTitles(Func<IReadOnlyList<string>, bool> accept, string description) =>
        Ui.WaitForValue(ReadTabTitles, accept, description);

    /// <summary>Waits until the tab with this title reports this ItemStatus (<c>saved</c>, <c>unsaved</c>, <c>error</c>).</summary>
    public void WaitForTabStatus(string title, string status, TimeSpan? timeout = null) =>
        Ui.WaitForValue(
            () => Ui.StatusOf(FindTab(title)),
            value => value == status,
            $"the tab {Ui.Describe(title)} to report ItemStatus {Ui.Describe(status)}",
            timeout);

    /// <summary>
    /// The scenario's "wait more than 1.5 s" after the last edit (the autosave debounce is 400 ms with a 1 s max-wait),
    /// then confirms the tab reports <c>saved</c>, so the text is in the database before the app closes or dies.
    /// </summary>
    public void WaitForAutosave(string tabTitle, Stopwatch sinceLastEdit)
    {
        Ui.WaitUntil(() => sinceLastEdit.Elapsed > AutosaveGrace, "the 1.5 s autosave grace period", AutosaveGrace + Ui.Timeout);
        WaitForTabStatus(tabTitle, ItemStatuses.Saved);
    }

    // ------------------------------------------------------------------ ending

    /// <summary>
    /// Closes the app the way a user does, through the window's close button (contract: CloseButton, invoked through UI
    /// Automation; Alt+F4 when it is missing), and waits for the process to exit.
    /// </summary>
    public void Close()
    {
        ThrowIfExited();
        if (FindByAutomationId(AutomationIds.CloseButton)?.Patterns.Invoke.PatternOrDefault is { } invoke)
        {
            invoke.Invoke();
        }
        else
        {
            PressShortcut(VirtualKeyShort.ALT, VirtualKeyShort.F4);
        }

        if (!_process.WaitForExit(Ui.ExitTimeout))
        {
            var dialog = SafeFindWindow(AutomationIds.ConfirmDialog) is null ? string.Empty : $" A {AutomationIds.ConfirmDialog} is open.";
            throw new TimeoutException($"NimPad did not exit within {Ui.ExitTimeout.TotalSeconds:0} s after a graceful close.{dialog}");
        }
    }

    /// <summary>Kills the process (crash simulation) and waits for it to be gone.</summary>
    public void Kill()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        if (!_process.WaitForExit(Ui.ExitTimeout))
        {
            throw new TimeoutException($"NimPad (pid {ProcessId}) did not end within {Ui.ExitTimeout.TotalSeconds:0} s after Kill.");
        }
    }

    /// <summary>Kills the process if it is still running and releases the automation client.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (!_process.HasExited)
            {
                Kill();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            // Already gone, or Windows refused; cleanup must not hide the test result.
        }

        Automation.Dispose();
        _process.Dispose();
    }

    // ------------------------------------------------------------------ helpers

    private static bool ContainsInOrder(IReadOnlyList<string> names, IReadOnlyList<string> expected)
    {
        var next = 0;
        foreach (var name in names)
        {
            if (next < expected.Count && name == expected[next])
            {
                next++;
            }
        }

        return next == expected.Count;
    }

    private static int TabIndexOf(AutomationElement element)
    {
        var id = Ui.IdOf(element);
        var match = id is null ? null : TabId().Match(id);
        return match is { Success: true } ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : -1;
    }

    [GeneratedRegex(@"^Tab_(\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex TabId();

    private Window? FindMainWindow()
    {
        ThrowIfExited();
        var conditions = Automation.ConditionFactory;
        var window = Automation.GetDesktop().FindFirstChild(
            conditions.ByProcessId(ProcessId).And(conditions.ByAutomationId(AutomationIds.MainWindow)));
        return window is not null && Ui.IsShown(window) ? window.AsWindow() : null;
    }

    private bool IsStarted() =>
        FindTabs().Count > 0 || Ui.IsShown(FindByAutomationId(AutomationIds.EmptyStateNewNote));

    private void ResizeWindow()
    {
        if (MainWindow.Patterns.Window.PatternOrDefault is { } window
            && window.WindowVisualState.ValueOrDefault != WindowVisualState.Normal)
        {
            window.SetWindowVisualState(WindowVisualState.Normal);
        }

        var width = Math.Min(PreferredWindowSize.Width, User32.GetSystemMetrics(SystemMetric.SM_CXSCREEN));
        var height = Math.Min(PreferredWindowSize.Height, User32.GetSystemMetrics(SystemMetric.SM_CYSCREEN) - TaskbarAllowance);
        User32.SetWindowPos(
            _mainWindowHandle,
            IntPtr.Zero,
            0,
            0,
            width,
            height,
            SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE);
        Ui.WaitForValue(
            () => MainWindow.BoundingRectangle,
            bounds => Math.Abs(bounds.Width - width) <= 2 && Math.Abs(bounds.Height - height) <= 2,
            $"the main window to be {width}x{height}");
    }

    private bool IsFocusInApp()
    {
        try
        {
            return Automation.FocusedElement() is { } focused && focused.Properties.ProcessId.ValueOrDefault == ProcessId;
        }
        catch (Exception ex) when (Ui.IsTransient(ex))
        {
            return false;
        }
    }

    private string? TryReadEditorText() => FindByAutomationId(AutomationIds.Editor)?.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault;

    private AutomationElement? SafeFindWindow(string automationId)
    {
        try
        {
            return FindWindow(automationId);
        }
        catch (Exception ex) when (Ui.IsTransient(ex) || ex is InvalidOperationException)
        {
            return null;
        }
    }

    private void ThrowIfExited()
    {
        if (_process.HasExited)
        {
            throw new InvalidOperationException($"NimPad (pid {ProcessId}) exited unexpectedly with code {_process.ExitCode}.");
        }
    }
}
