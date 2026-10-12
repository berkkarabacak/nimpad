using System.Windows;
using NimPad.Presentation.Shortcuts;

namespace NimPad.App.Shell;

/// <summary>
/// The keyboard shortcuts dialog (design page 12, Ctrl+/). Focus starts in the filter field; Esc clears a filled filter,
/// then closes (through <see cref="ModalLayer"/>).
/// </summary>
public partial class ShortcutsDialog : IModalDialog
{
    private FrameworkElement? _host;

    /// <summary>Builds the dialog over <paramref name="shortcuts"/>.</summary>
    public ShortcutsDialog(ShortcutsViewModel shortcuts)
    {
        ArgumentNullException.ThrowIfNull(shortcuts);
        InitializeComponent();
        DataContext = shortcuts;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <inheritdoc/>
    public event EventHandler? Finished;

    /// <inheritdoc/>
    public bool CancelOnScrimClick => true;

    /// <inheritdoc/>
    public void FocusInitialElement() => FilterBox.FocusAndSelectAll();

    /// <inheritdoc/>
    public void Cancel()
    {
        var finished = Finished;
        Finished = null;
        finished?.Invoke(this, EventArgs.Empty);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Cancel();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _host = Parent as FrameworkElement;
        if (_host is not null)
        {
            _host.SizeChanged += OnHostSizeChanged;
            FitToHost();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_host is not null)
        {
            _host.SizeChanged -= OnHostSizeChanged;
            _host = null;
        }
    }

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e) => FitToHost();

    // The dialog keeps 24 px around it and scrolls its list when the window is short.
    private void FitToHost()
    {
        if (_host is { ActualHeight: > 0 } host)
        {
            MaxHeight = Math.Max(160, host.ActualHeight - Margin.Top - Margin.Bottom);
        }
    }
}
