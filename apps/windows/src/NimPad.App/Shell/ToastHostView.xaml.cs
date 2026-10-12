using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NimPad.Presentation.Feedback;

namespace NimPad.App.Shell;

/// <summary>
/// The toast stack (design pages 10 and 25). It never moves focus; a toast's countdown pauses while the pointer is
/// over it or keyboard focus is inside it, and each new toast is announced to screen readers (polite live region).
/// </summary>
public partial class ToastHostView : UserControl
{
    /// <summary>Creates the host.</summary>
    public ToastHostView()
    {
        InitializeComponent();
    }

    private static ToastViewModel? ToastOf(object sender) => (sender as FrameworkElement)?.DataContext as ToastViewModel;

    private void OnToastLoaded(object sender, RoutedEventArgs e) => BannerHostView.AnnounceLiveRegion(sender as UIElement);

    private void OnToastPointerEnter(object sender, MouseEventArgs e) => ToastOf(sender)?.Pause();

    private void OnToastPointerLeave(object sender, MouseEventArgs e)
    {
        if (sender is UIElement { IsKeyboardFocusWithin: false })
        {
            ToastOf(sender)?.Resume();
        }
    }

    private void OnToastFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            ToastOf(sender)?.Pause();
        }
        else if (sender is UIElement { IsMouseOver: false })
        {
            ToastOf(sender)?.Resume();
        }
    }
}
