using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace NimPad.App.Shell;

/// <summary>
/// A <see cref="Border"/> that appears in the UI Automation tree (a plain Border or panel does not), so a region of the
/// window (title bar, status bar, banner and toast hosts, one toast) can carry an
/// <see cref="System.Windows.Automation.AutomationProperties.AutomationId"/>, a Name and an ItemStatus for screen
/// readers and the UI tests. <see cref="ControlType"/> picks how it is announced.
/// </summary>
public class RegionBorder : Border
{
    /// <summary>The <see cref="ControlType"/> property.</summary>
    public static readonly DependencyProperty ControlTypeProperty = DependencyProperty.Register(
        nameof(ControlType), typeof(AutomationControlType), typeof(RegionBorder), new FrameworkPropertyMetadata(AutomationControlType.Pane));

    /// <summary>The automation control type (default <see cref="AutomationControlType.Pane"/>).</summary>
    public AutomationControlType ControlType
    {
        get => (AutomationControlType)GetValue(ControlTypeProperty);
        set => SetValue(ControlTypeProperty, value);
    }

    /// <inheritdoc/>
    protected override AutomationPeer OnCreateAutomationPeer() => new RegionBorderAutomationPeer(this);

    private sealed class RegionBorderAutomationPeer(RegionBorder owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => owner.ControlType;

        protected override string GetClassNameCore() => nameof(RegionBorder);

        protected override bool IsControlElementCore() => true;

        protected override bool IsContentElementCore() => true;
    }
}
