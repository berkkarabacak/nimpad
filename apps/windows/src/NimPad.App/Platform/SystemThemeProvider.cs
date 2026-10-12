using NimPad.App.Theming;
using NimPad.Presentation.Platform;

namespace NimPad.App.Platform;

/// <summary>Exposes a <see cref="SystemThemeWatcher"/> (the one <see cref="ThemeService"/> owns) as an <see cref="ISystemThemeProvider"/>.</summary>
public sealed class SystemThemeProvider : ISystemThemeProvider
{
    private readonly SystemThemeWatcher _watcher;

    /// <summary>Wraps <paramref name="watcher"/>.</summary>
    public SystemThemeProvider(SystemThemeWatcher watcher)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        _watcher = watcher;
    }

    /// <inheritdoc/>
    public bool IsDarkMode => _watcher.IsDarkMode;

    /// <inheritdoc/>
    public event EventHandler? Changed
    {
        add => _watcher.Changed += value;
        remove => _watcher.Changed -= value;
    }
}
