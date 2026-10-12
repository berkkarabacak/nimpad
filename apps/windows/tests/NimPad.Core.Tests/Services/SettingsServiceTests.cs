using NimPad.Core.Domain;
using NimPad.Core.Services;
using NimPad.Core.Tests.Support;
using Xunit;

namespace NimPad.Core.Tests.Services;

public sealed class SettingsServiceTests : IAsyncLifetime
{
    private TestStore _store = null!;

    private SettingsService Settings => _store.Settings;

    public async ValueTask InitializeAsync() => _store = await TestStore.CreateAsync();

    public ValueTask DisposeAsync()
    {
        _store.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task AFreshDatabaseHasTheDefaults()
    {
        Assert.Equal(AppSettings.Default, await Settings.LoadAsync());
    }

    [Fact]
    public async Task SettingsRoundTrip()
    {
        var settings = new AppSettings
        {
            Theme = ThemePreference.Dark,
            EditorFontFamily = "Consolas",
            EditorFontSize = 16,
            WordWrap = true,
            ShowLineNumbers = false,
            TabSize = 2,
            RestoreTabs = false,
            StartWithWindows = true,
            GlobalHotkey = "Ctrl+Shift+Space",
            Zoom = 125,
        };

        await Settings.SaveAsync(settings);

        Assert.Equal(settings, await Settings.LoadAsync());
        Assert.Equal(
            ["\"dark\""],
            await _store.QueryAsync("SELECT value FROM settings WHERE key = $k;", r => r.GetString(0), ("$k", SettingsService.Keys.Theme)));
    }

    [Fact]
    public async Task MissingKeysTakeTheirDefaults()
    {
        await _store.ExecuteRawAsync(
            "INSERT INTO settings (key, value, updated_at) VALUES ($k, 'true', '2026-01-01T00:00:00.000Z');",
            ("$k", SettingsService.Keys.WordWrap));

        Assert.Equal(AppSettings.Default with { WordWrap = true }, await Settings.LoadAsync());
    }

    [Fact]
    public async Task InvalidValuesFallBackOneByOne()
    {
        await _store.ExecuteRawAsync(
            """
            INSERT INTO settings (key, value, updated_at) VALUES
                ('appearance.theme', '"high-contrast"', '2026-01-01T00:00:00.000Z'),
                ('editor.tabSize', '3', '2026-01-01T00:00:00.000Z'),
                ('editor.fontSize', '"large"', '2026-01-01T00:00:00.000Z'),
                ('editor.zoom', '1000', '2026-01-01T00:00:00.000Z'),
                ('editor.fontFamily', '"Fira Code"', '2026-01-01T00:00:00.000Z'),
                ('editor.lineNumbers', 'false', '2026-01-01T00:00:00.000Z');
            """);

        var settings = await Settings.LoadAsync();

        Assert.Equal(AppSettings.Default with { EditorFontFamily = "Fira Code", ShowLineNumbers = false }, settings);
    }

    [Fact]
    public async Task SavingKeepsSettingsFromANewerVersion()
    {
        await _store.ExecuteRawAsync(
            "INSERT INTO settings (key, value, updated_at) VALUES ('general.minimizeToTray', 'true', '2026-01-01T00:00:00.000Z');");

        await Settings.SaveAsync(AppSettings.Default with { Zoom = 150 });
        await Settings.SaveAsync(AppSettings.Default with { Zoom = 175 });

        Assert.Equal(175, (await Settings.LoadAsync()).Zoom);
        Assert.Equal(["true"], await _store.QueryAsync("SELECT value FROM settings WHERE key = 'general.minimizeToTray';", r => r.GetString(0)));
        Assert.Equal(11, await _store.CountAsync("SELECT count(*) FROM settings;"));
    }

    [Fact]
    public async Task InvalidSettingsAreNotSaved()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Settings.SaveAsync(AppSettings.Default with { TabSize = 5 }));
        await Assert.ThrowsAsync<ArgumentException>(() => Settings.SaveAsync(AppSettings.Default with { EditorFontSize = 2 }));
        Assert.Equal(0, await _store.CountAsync("SELECT count(*) FROM settings;"));
    }

    [Fact]
    public async Task AnEmptyHotkeyTurnsTheShortcutOff()
    {
        await Settings.SaveAsync(AppSettings.Default with { GlobalHotkey = string.Empty });
        Assert.Equal(string.Empty, (await Settings.LoadAsync()).GlobalHotkey);
    }
}
