using System.Runtime.Versioning;
using System.Windows;
using NimPad.App.Themes;

namespace NimPad.App.Theming;

/// <summary>The user's theme choice. <see cref="System"/> follows Windows' app mode.</summary>
/// <remarks>
/// WPF 9+ also has a <c>System.Windows.ThemeMode</c> struct (and a <c>Window.ThemeMode</c> property): files that
/// import both namespaces should alias this one, e.g. <c>using ThemeMode = NimPad.App.Theming.ThemeMode;</c>.
/// </remarks>
public enum ThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Applies the light or dark token set. The token dictionary is kept as the FIRST entry of the target resources'
/// MergedDictionaries and swapped in place; every other dictionary references tokens with DynamicResource, so the
/// whole UI recolors live. In <see cref="ThemeMode.System"/> mode it follows <see cref="SystemTheme"/>.
/// Create it on the UI thread; it is not thread-safe.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ThemeService : IDisposable
{
    private readonly ResourceDictionary _resources;
    private readonly bool _ownsSystemTheme;
    private ResourceDictionary? _light;
    private ResourceDictionary? _dark;
    private bool _applied;
    private bool _disposed;

    /// <summary>Targets <c>Application.Current.Resources</c> and watches the Windows app mode.</summary>
    public ThemeService()
        : this(
            Application.Current?.Resources
                ?? throw new InvalidOperationException("ThemeService() needs an Application; pass the resources."),
            new SystemThemeWatcher(),
            ownsSystemTheme: true)
    {
        Current = this;
    }

    /// <summary>Targets <paramref name="resources"/> (for example a window's or a test's dictionary).</summary>
    /// <param name="resources">Dictionary whose first merged dictionary holds the tokens.</param>
    /// <param name="systemTheme">Source of the Windows app mode; the service does not dispose it.</param>
    public ThemeService(ResourceDictionary resources, SystemThemeWatcher systemTheme)
        : this(resources, systemTheme, ownsSystemTheme: false)
    {
    }

    private ThemeService(ResourceDictionary resources, SystemThemeWatcher systemTheme, bool ownsSystemTheme)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(systemTheme);
        _resources = resources;
        SystemTheme = systemTheme;
        _ownsSystemTheme = ownsSystemTheme;
        SystemTheme.Changed += OnSystemThemeChanged;
    }

    /// <summary>The service created for the application resources (null until one exists).</summary>
    public static ThemeService? Current { get; private set; }

    /// <summary>The Windows app-mode source (ISystemThemeProvider-compatible: IsDarkMode + Changed).</summary>
    public SystemThemeWatcher SystemTheme { get; }

    /// <summary>The last mode passed to <see cref="Apply"/>.</summary>
    public ThemeMode Mode { get; private set; } = ThemeMode.System;

    /// <summary>True when the dark token set is applied.</summary>
    public bool IsDark { get; private set; }

    /// <summary>
    /// Also set DWMWA_USE_IMMERSIVE_DARK_MODE on every open window of the application when the theme changes, so
    /// standard frames and Windows 11 borders follow (default true).
    /// </summary>
    public bool UpdateWindowFrames { get; set; } = true;

    /// <summary>Raised after the applied token set changed (light to dark or back).</summary>
    public event EventHandler? ThemeChanged;

    /// <summary>Creates a fresh token dictionary for a theme.</summary>
    public static ResourceDictionary CreateTokens(bool dark) => dark ? new TokensDark() : new TokensLight();

    /// <summary>True when <paramref name="dictionary"/> is one of the NimPad token sets.</summary>
    public static bool IsTokenDictionary(ResourceDictionary? dictionary) => dictionary is TokensLight or TokensDark;

    /// <summary>
    /// Merges the token set (first) and the design system (<see cref="DesignSystem"/>) into
    /// <paramref name="resources"/> unless they are already there. Used by App.xaml-less hosts such as tests.
    /// </summary>
    public static void InstallDesignSystem(ResourceDictionary resources, bool dark)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var merged = resources.MergedDictionaries;
        if (merged.Count > 0 && IsTokenDictionary(merged[0]))
        {
            merged[0] = CreateTokens(dark);
        }
        else
        {
            merged.Insert(0, CreateTokens(dark));
        }

        if (!merged.OfType<DesignSystem>().Any())
        {
            merged.Insert(1, new DesignSystem());
        }
    }

    /// <summary>Applies <paramref name="mode"/>; System resolves through <see cref="SystemTheme"/>.</summary>
    public void Apply(ThemeMode mode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Mode = mode;
        Update();
    }

    /// <summary>
    /// Matches a window's native frame to the applied theme (DWM immersive dark mode). Call it for windows created
    /// after the last theme change, e.g. in the Settings window's constructor; deferred until the window has a handle.
    /// </summary>
    public void ApplyToWindow(Window window) => WindowThemeHelper.ApplyImmersiveDarkMode(window, IsDark);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemTheme.Changed -= OnSystemThemeChanged;
        if (_ownsSystemTheme)
        {
            SystemTheme.Dispose();
        }

        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }

        ThemeChanged = null;
    }

    private void OnSystemThemeChanged(object? sender, EventArgs e)
    {
        if (!_disposed && Mode == ThemeMode.System)
        {
            Update();
        }
    }

    private void Update()
    {
        var dark = Mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => SystemTheme.IsDarkMode,
        };
        var merged = _resources.MergedDictionaries;

        // Reuse the token set App.xaml (or a previous Apply) already merged, so startup does not invalidate twice.
        switch (merged.Count > 0 ? merged[0] : null)
        {
            case TokensDark existing when dark:
                _dark ??= existing;
                break;
            case TokensLight existing when !dark:
                _light ??= existing;
                break;
        }

        var tokens = dark ? (_dark ??= CreateTokens(dark: true)) : (_light ??= CreateTokens(dark: false));
        if (merged.Count > 0 && IsTokenDictionary(merged[0]))
        {
            if (!ReferenceEquals(merged[0], tokens))
            {
                merged[0] = tokens;
            }
        }
        else
        {
            merged.Insert(0, tokens);
        }

        var changed = !_applied || dark != IsDark;
        _applied = true;
        IsDark = dark;
        if (!changed)
        {
            return;
        }

        if (UpdateWindowFrames)
        {
            ApplyToWindows(dark);
        }

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void ApplyToWindows(bool dark)
    {
        var app = Application.Current;
        if (app is null || !app.Dispatcher.CheckAccess())
        {
            return;
        }

        foreach (Window window in app.Windows)
        {
            WindowThemeHelper.ApplyImmersiveDarkMode(window, dark);
        }
    }
}
