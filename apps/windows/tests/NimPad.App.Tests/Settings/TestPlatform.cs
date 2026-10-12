using System.Windows.Threading;
using NimPad.Presentation.Platform;

namespace NimPad.App.Tests.Settings;

/// <summary>Platform services for App tests: the real WPF dispatcher of the test's STA thread and scriptable fakes.</summary>
internal sealed class TestPlatform
{
    public TestPlatform(IGlobalHotkeyService? hotkey = null)
    {
        Dispatcher = new WpfDispatcher(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        GlobalHotkey = hotkey ?? Hotkeys;
        Services = new PlatformServices
        {
            Dispatcher = Dispatcher,
            Clipboard = new NullClipboard(),
            FileDialogs = new NoFileDialogs(),
            Dialogs = new CancelDialogs(),
            SystemTheme = SystemTheme,
            Autostart = Autostart,
            GlobalHotkey = GlobalHotkey,
            Fonts = new Fonts(),
            Shell = new NoShell(),
        };
    }

    public PlatformServices Services { get; }

    public WpfDispatcher Dispatcher { get; }

    public IGlobalHotkeyService GlobalHotkey { get; }

    public FakeHotkeys Hotkeys { get; } = new();

    public FakeSystemTheme SystemTheme { get; } = new();

    public FakeAutostart Autostart { get; } = new();

    private sealed class NullClipboard : IClipboardService
    {
        public void SetText(string text)
        {
        }
    }

    private sealed class NoFileDialogs : IFileDialogService
    {
        public IReadOnlyList<string> PickFilesToOpen() => [];

        public string? PickSaveFile(string suggestedName, string? initialDirectory) => null;
    }

    private sealed class CancelDialogs : IDialogService
    {
        public Task<bool> ConfirmAsync(ConfirmRequest request) => Task.FromResult(false);

        public Task<DialogChoice> ChooseAsync(ConfirmRequest request) => Task.FromResult(DialogChoice.Cancel);
    }

    private sealed class Fonts : IFontCatalog
    {
        public IReadOnlyList<string> MonospaceFamilies { get; } = ["Cascadia Mono", "Consolas", "Courier New"];
    }

    private sealed class NoShell : IShellLauncher
    {
        public void OpenUri(Uri uri)
        {
        }
    }
}

/// <summary><see cref="IUiDispatcher"/> over a WPF <see cref="System.Windows.Threading.Dispatcher"/> (as the App does).</summary>
internal sealed class WpfDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public bool CheckAccess() => dispatcher.CheckAccess();

    public void Post(Action action) => dispatcher.BeginInvoke(action);

    public Task InvokeAsync(Func<Task> action) => dispatcher.CheckAccess() ? action() : dispatcher.InvokeAsync(action).Task.Unwrap();
}

internal sealed class FakeHotkeys : IGlobalHotkeyService
{
    public HashSet<string> TakenByOtherApps { get; } = new(StringComparer.Ordinal);

    public string? Registered { get; private set; }

    public bool TryRegister(string gesture, out string? error)
    {
        if (TakenByOtherApps.Contains(gesture))
        {
            error = $"Another app already uses {gesture}. Try a different shortcut.";
            return false;
        }

        Registered = gesture;
        error = null;
        return true;
    }

    public void Unregister() => Registered = null;
}

internal sealed class FakeSystemTheme : ISystemThemeProvider
{
    public bool IsDarkMode { get; set; }

    public event EventHandler? Changed;

    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}

internal sealed class FakeAutostart : IAutostartService
{
    public bool IsEnabled { get; private set; }

    public void Set(bool enabled) => IsEnabled = enabled;
}
