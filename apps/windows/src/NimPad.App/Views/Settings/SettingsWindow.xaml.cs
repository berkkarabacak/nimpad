using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NimPad.App.Theming;
using NimPad.Presentation.Integration;
using NimPad.Presentation.Settings;

namespace NimPad.App.Views.Settings;

/// <summary>
/// The Settings window (see the XAML header). Its DataContext is the shell's <see cref="SettingsViewModel"/>; open it
/// with <see cref="SettingsWindowLauncher.Show"/>. Esc closes it (unless a dropdown or the shortcut capture takes the
/// key); Up/Down in the section list switch pages; while the global shortcut is being captured, the next key chord
/// goes to <see cref="SettingsViewModel.CompleteShortcutCapture"/> and Esc cancels.
/// </summary>
public partial class SettingsWindow : Window
{
    private SettingsViewModel? _observed;

    /// <summary>Creates the window; set the DataContext to a <see cref="SettingsViewModel"/>.</summary>
    public SettingsWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Observe(ViewModel);
        Loaded += (_, _) => FocusNavigation();
    }

    /// <summary>Creates the window for <paramref name="viewModel"/>.</summary>
    public SettingsWindow(SettingsViewModel viewModel)
        : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
    }

    /// <summary>The view model, when the DataContext is one.</summary>
    public SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    /// <summary>Moves keyboard focus to the selected section, so Up/Down switch sections at once.</summary>
    public void FocusNavigation()
    {
        if (Nav.SelectedItem is ListBoxItem item)
        {
            item.Focus();
        }
        else
        {
            Nav.Focus();
        }
    }

    /// <summary>
    /// Handles a key while the global shortcut is captured: Esc (alone) cancels, a modifier alone keeps waiting, any
    /// other key completes the chord (an invalid or taken one shows its error and capturing continues).
    /// </summary>
    /// <returns><see langword="true"/> when the key was consumed by the capture.</returns>
    internal bool HandleCaptureKey(Key key, ModifierKeys modifiers)
    {
        if (ViewModel is not { IsCapturingShortcut: true } viewModel)
        {
            return false;
        }

        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            viewModel.CancelShortcutCapture();
            ChangeShortcutButton.Focus();
            return true;
        }

        var gesture = HotkeyVirtualKeys.ComposeGesture(
            modifiers.HasFlag(ModifierKeys.Control),
            modifiers.HasFlag(ModifierKeys.Alt),
            modifiers.HasFlag(ModifierKeys.Shift),
            modifiers.HasFlag(ModifierKeys.Windows),
            KeyInterop.VirtualKeyFromKey(key),
            key.ToString());
        if (gesture is not null && viewModel.CompleteShortcutCapture(gesture))
        {
            ChangeShortcutButton.Focus();
        }

        return true;
    }

    /// <inheritdoc/>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (ViewModel is { IsCapturingShortcut: true })
        {
            var key = e.Key switch
            {
                Key.System => e.SystemKey,
                Key.ImeProcessed => e.ImeProcessedKey,
                Key.DeadCharProcessed => e.DeadCharProcessedKey,
                _ => e.Key,
            };
            if (HandleCaptureKey(key, Keyboard.Modifiers))
            {
                e.Handled = true;
                return;
            }
        }

        base.OnPreviewKeyDown(e);
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnKeyDown(e);

        // An open dropdown handles Esc itself (it closes); only an unhandled Esc closes the window.
        if (!e.Handled && e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            Close();
        }
    }

    /// <inheritdoc/>
    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        // Keys pressed in another window are not meant for the capture.
        if (ViewModel is { IsCapturingShortcut: true } viewModel)
        {
            viewModel.CancelShortcutCapture();
        }
    }

    /// <inheritdoc/>
    protected override void OnClosed(EventArgs e)
    {
        if (ViewModel is { IsCapturingShortcut: true } viewModel)
        {
            viewModel.CancelShortcutCapture();
        }

        Observe(null);
        base.OnClosed(e);
    }

    private void OnChangeShortcutClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        if (viewModel.IsCapturingShortcut)
        {
            viewModel.CancelShortcutCapture();
        }
        else
        {
            viewModel.BeginShortcutCapture();
        }
    }

    private void OnSectionChanged(object sender, SelectionChangedEventArgs e) => PageScroller?.ScrollToTop();

    // The standard frame follows the theme (ThemeService also updates every open window; this covers windows shown
    // without one, such as in tests).
    private void Observe(SettingsViewModel? viewModel)
    {
        if (_observed is not null)
        {
            _observed.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _observed = viewModel;
        if (viewModel is not null)
        {
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            WindowThemeHelper.ApplyImmersiveDarkMode(this, viewModel.EffectiveIsDark);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.EffectiveIsDark) && sender is SettingsViewModel viewModel)
        {
            WindowThemeHelper.ApplyImmersiveDarkMode(this, viewModel.EffectiveIsDark);
        }
    }
}
