using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace NimPad.App.Shell;

/// <summary>The banners under the tab strip (design pages 13 and 25); each one is announced to screen readers once.</summary>
public partial class BannerHostView : UserControl
{
    /// <summary>Creates the host.</summary>
    public BannerHostView()
    {
        InitializeComponent();
    }

    private void OnBannerLoaded(object sender, RoutedEventArgs e) => AnnounceLiveRegion(sender as UIElement);

    /// <summary>Raises the UI Automation live-region event for <paramref name="element"/> (polite announcement).</summary>
    internal static void AnnounceLiveRegion(UIElement? element)
    {
        if (element is null || !AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            return;
        }

        var peer = UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
