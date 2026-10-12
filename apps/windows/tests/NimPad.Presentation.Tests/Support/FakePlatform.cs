using NimPad.Presentation.Platform;

namespace NimPad.Presentation.Tests.Support;

/// <summary>Scriptable platform services that record what the view models asked for.</summary>
internal sealed class FakePlatform
{
    public FakePlatform(IUiDispatcher dispatcher)
    {
        Services = new PlatformServices
        {
            Dispatcher = dispatcher,
            Clipboard = Clipboard,
            FileDialogs = FileDialogs,
            Dialogs = Dialogs,
            SystemTheme = SystemTheme,
            Autostart = Autostart,
            GlobalHotkey = Hotkey,
            Fonts = Fonts,
            Shell = Shell,
        };
    }

    public PlatformServices Services { get; }

    public FakeClipboard Clipboard { get; } = new();

    public FakeFileDialogs FileDialogs { get; } = new();

    public FakeDialogs Dialogs { get; } = new();

    public FakeSystemTheme SystemTheme { get; } = new();

    public FakeAutostart Autostart { get; } = new();

    public FakeHotkeys Hotkey { get; } = new();

    public FakeFonts Fonts { get; } = new();

    public FakeShell Shell { get; } = new();
}

internal sealed class FakeClipboard : IClipboardService
{
    public string? Text { get; private set; }

    /// <summary>Makes SetText throw like WPF's clipboard when another app holds it.</summary>
    public bool Fail { get; set; }

    public void SetText(string text)
    {
        if (Fail)
        {
            throw new System.Runtime.InteropServices.COMException("OpenClipboard Failed (CLIPBRD_E_CANT_OPEN)", unchecked((int)0x800401D0));
        }

        Text = text;
    }
}

internal sealed class FakeFileDialogs : IFileDialogService
{
    public Queue<IReadOnlyList<string>> FilesToOpen { get; } = new();

    public Queue<string?> SavePaths { get; } = new();

    public List<(string SuggestedName, string? InitialDirectory)> SaveRequests { get; } = [];

    public IReadOnlyList<string> PickFilesToOpen() => FilesToOpen.Count > 0 ? FilesToOpen.Dequeue() : [];

    public string? PickSaveFile(string suggestedName, string? initialDirectory)
    {
        SaveRequests.Add((suggestedName, initialDirectory));
        return SavePaths.Count > 0 ? SavePaths.Dequeue() : null;
    }
}

internal sealed class FakeDialogs : IDialogService
{
    public List<ConfirmRequest> Requests { get; } = [];

    public Queue<DialogChoice> Answers { get; } = new();

    public DialogChoice DefaultAnswer { get; set; } = DialogChoice.Cancel;

    public Task<bool> ConfirmAsync(ConfirmRequest request)
    {
        Requests.Add(request);
        return Task.FromResult(Next() == DialogChoice.Primary);
    }

    public Task<DialogChoice> ChooseAsync(ConfirmRequest request)
    {
        Requests.Add(request);
        return Task.FromResult(Next());
    }

    private DialogChoice Next() => Answers.Count > 0 ? Answers.Dequeue() : DefaultAnswer;
}

internal sealed class FakeSystemTheme : ISystemThemeProvider
{
    public bool IsDarkMode { get; private set; }

    public event EventHandler? Changed;

    public void Set(bool dark)
    {
        IsDarkMode = dark;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class FakeAutostart : IAutostartService
{
    public bool IsEnabled { get; private set; }

    public bool Fail { get; set; }

    public void Set(bool enabled)
    {
        if (Fail)
        {
            throw new UnauthorizedAccessException("Registry access denied.");
        }

        IsEnabled = enabled;
    }
}

internal sealed class FakeHotkeys : IGlobalHotkeyService
{
    public HashSet<string> TakenByOtherApps { get; } = new(StringComparer.Ordinal);

    public string? Registered { get; private set; }

    public List<string> Attempts { get; } = [];

    /// <summary>Makes TryRegister throw (a platform failure rather than a conflict).</summary>
    public bool Fail { get; set; }

    public bool TryRegister(string gesture, out string? error)
    {
        Attempts.Add(gesture);
        if (Fail)
        {
            throw new InvalidOperationException("RegisterHotKey failed.");
        }

        if (TakenByOtherApps.Contains(gesture))
        {
            error = $"{gesture} is already in use.";
            return false;
        }

        Registered = gesture;
        error = null;
        return true;
    }

    public void Unregister() => Registered = null;
}

internal sealed class FakeFonts : IFontCatalog
{
    public IReadOnlyList<string> MonospaceFamilies { get; set; } = ["Consolas", "Cascadia Mono", "Courier New"];
}

internal sealed class FakeShell : IShellLauncher
{
    public List<Uri> Opened { get; } = [];

    public void OpenUri(Uri uri) => Opened.Add(uri);
}
