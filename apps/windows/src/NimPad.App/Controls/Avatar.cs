using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace NimPad.App.Controls;

/// <summary>Sync status shown as a dot on the account <see cref="Avatar"/> (deck page 24).</summary>
public enum AvatarStatus
{
    /// <summary>No dot (synced, or Phase 1 local-only).</summary>
    None,

    /// <summary>Accent dot.</summary>
    Syncing,

    /// <summary>Text3 dot.</summary>
    Offline,

    /// <summary>Warn dot.</summary>
    Conflict,

    /// <summary>Err dot.</summary>
    Error,
}

/// <summary>
/// The account avatar: a 28 px circle with AvatarBg/AvatarFg initials (or a photo via <see cref="Source"/>) and an
/// optional status dot ringed in <see cref="StatusRingBrush"/> (Chrome by default, matching the title bar).
/// Status is never color alone: pair it with text (tooltip or status bar). Size follows Width/Height.
/// </summary>
public class Avatar : Control
{
    public static readonly DependencyProperty InitialsProperty = DependencyProperty.Register(
        nameof(Initials), typeof(string), typeof(Avatar), new FrameworkPropertyMetadata(string.Empty));

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(Avatar), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(AvatarStatus), typeof(Avatar), new FrameworkPropertyMetadata(AvatarStatus.None));

    public static readonly DependencyProperty StatusRingBrushProperty = DependencyProperty.Register(
        nameof(StatusRingBrush), typeof(Brush), typeof(Avatar), new FrameworkPropertyMetadata(null));

    static Avatar()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(Avatar), new FrameworkPropertyMetadata(typeof(Avatar)));
        FocusableProperty.OverrideMetadata(typeof(Avatar), new FrameworkPropertyMetadata(false));
    }

    /// <summary>One or two letters, e.g. "BK".</summary>
    public string Initials
    {
        get => (string)GetValue(InitialsProperty);
        set => SetValue(InitialsProperty, value);
    }

    /// <summary>Optional photo; replaces the initials when set.</summary>
    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public AvatarStatus Status
    {
        get => (AvatarStatus)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    /// <summary>Ring around the status dot; set it to the surface the avatar sits on (default Chrome).</summary>
    public Brush? StatusRingBrush
    {
        get => (Brush?)GetValue(StatusRingBrushProperty);
        set => SetValue(StatusRingBrushProperty, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new AvatarAutomationPeer(this);

    private sealed class AvatarAutomationPeer(Avatar owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override string GetClassNameCore() => nameof(Avatar);

        protected override string GetNameCore()
        {
            var name = base.GetNameCore();
            return string.IsNullOrEmpty(name) ? ((Avatar)Owner).Initials : name;
        }

        protected override string GetItemStatusCore()
        {
            var status = ((Avatar)Owner).Status;
            return status == AvatarStatus.None ? base.GetItemStatusCore() : status.ToString();
        }

        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}
