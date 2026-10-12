using System.Runtime.InteropServices;
using NimPad.App.Integration;
using NimPad.App.Tests.Themes;
using Xunit;

namespace NimPad.App.Tests.Integration;

/// <summary>
/// RegisterHotKey for real, with combinations nobody uses (F22 and F23 with three modifiers), and WM_HOTKEY delivered
/// to the service's message window.
/// </summary>
public sealed class GlobalHotkeyServiceTests
{
    private const string Rare = "Ctrl+Alt+Shift+F23";
    private const string OtherRare = "Ctrl+Alt+Shift+F22";

    [Fact]
    public void RegistersReplacesAndUnregistersAShortcut()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            using var service = new GlobalHotkeyService();

            Assert.True(service.TryRegister("shift+alt+ctrl+f23", out var error), error);
            Assert.Null(error);
            Assert.Equal(Rare, service.Gesture);
            var firstId = service.RegisteredId;
            Assert.NotEqual(0, firstId);

            // The same gesture again is not a conflict with ourselves.
            Assert.True(service.TryRegister(Rare, out _));
            Assert.Equal(firstId, service.RegisteredId);

            Assert.True(service.TryRegister(OtherRare, out _));
            Assert.Equal(OtherRare, service.Gesture);
            Assert.NotEqual(firstId, service.RegisteredId);

            // The replaced gesture was released: another registration can take it.
            using (var other = new GlobalHotkeyService())
            {
                Assert.True(other.TryRegister(Rare, out _));
            }

            service.Unregister();
            Assert.Null(service.Gesture);
            Assert.Equal(0, service.RegisteredId);
        });
    }

    [Fact]
    public void AShortcutTakenElsewhereFailsWithAReasonAndKeepsThePreviousOne()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            using var owner = new GlobalHotkeyService();
            using var service = new GlobalHotkeyService();
            Assert.True(owner.TryRegister(Rare, out _));
            Assert.True(service.TryRegister(OtherRare, out _));

            Assert.False(service.TryRegister(Rare, out var error));

            Assert.Equal($"Another app already uses {Rare}. Try a different shortcut.", error);
            Assert.Equal(OtherRare, service.Gesture);
            Assert.NotEqual(0, service.RegisteredId);

            Assert.False(service.TryRegister("Ctrl+A+B", out error));
            Assert.Equal("Use one key together with Ctrl, Alt or Win.", error);
            Assert.Equal(OtherRare, service.Gesture);
        });
    }

    [Fact]
    public void PressingTheShortcutRaisesPressedOnTheUiThread()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            using var service = new GlobalHotkeyService();
            var thread = Environment.CurrentManagedThreadId;
            var presses = new List<int>();
            service.Pressed += (_, _) => presses.Add(Environment.CurrentManagedThreadId);
            Assert.True(service.TryRegister(Rare, out _));

            SendMessage(service.WindowHandle, WmHotkey, service.RegisteredId, IntPtr.Zero);
            SendMessage(service.WindowHandle, WmHotkey, service.RegisteredId + 1000, IntPtr.Zero);

            Assert.Equal([thread], presses);

            service.Unregister();
            SendMessage(service.WindowHandle, WmHotkey, 0, IntPtr.Zero);
            Assert.Single(presses);
        });
    }

    [Fact]
    public void WithNoHotkeyGesturesAreRememberedButNeverRegisteredWithWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            using var disabled = new GlobalHotkeyService(registerWithWindows: false);
            Assert.False(disabled.RegistersWithWindows);

            Assert.True(disabled.TryRegister(Rare, out var error));
            Assert.Null(error);
            Assert.Equal(Rare, disabled.Gesture);
            Assert.Equal(IntPtr.Zero, disabled.WindowHandle);

            // Windows never saw it, so a real registration of the same keys succeeds.
            using var real = new GlobalHotkeyService();
            Assert.True(real.TryRegister(Rare, out _));
        });
    }

    [Fact]
    public void CallsFromAnotherThreadAreMarshaledAndDisposeReleasesTheShortcut()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        StaAsync.Run(async () =>
        {
            var service = new GlobalHotkeyService();
            var (registered, error) = await Task.Run(() => (service.TryRegister(Rare, out var e), e));
            Assert.True(registered, error);
            Assert.Equal(Rare, service.Gesture);

            service.Dispose();
            Assert.Throws<ObjectDisposedException>(() => service.TryRegister(Rare, out _));
            using var next = new GlobalHotkeyService();
            Assert.True(next.TryRegister(Rare, out _));
        });
    }

    private const int WmHotkey = 0x0312;

    [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, nint wParam, IntPtr lParam);
}
