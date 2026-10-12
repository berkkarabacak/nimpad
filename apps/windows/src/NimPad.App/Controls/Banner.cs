using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NimPad.App.Controls;

/// <summary>Banner flavor: neutral information, warning (WarnSoft + Warn icon) or error (ErrSoft + Err icon).</summary>
public enum BannerKind
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// A banner under the tab strip (deck pages 13 and 25): icon, message (<c>Content</c>), at most one link-style
/// action and an optional dismiss button. 36 px minimum height. Keyed styles InfoBanner / WarningBanner /
/// ErrorBanner preset <see cref="Kind"/>; the default style reads it.
/// </summary>
public class Banner : ContentControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(BannerKind), typeof(Banner), new FrameworkPropertyMetadata(BannerKind.Info));

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(Geometry), typeof(Banner), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty ActionContentProperty = DependencyProperty.Register(
        nameof(ActionContent), typeof(object), typeof(Banner), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty ActionCommandProperty = DependencyProperty.Register(
        nameof(ActionCommand), typeof(ICommand), typeof(Banner), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty ActionCommandParameterProperty = DependencyProperty.Register(
        nameof(ActionCommandParameter), typeof(object), typeof(Banner), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty DismissCommandProperty = DependencyProperty.Register(
        nameof(DismissCommand), typeof(ICommand), typeof(Banner), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty DismissCommandParameterProperty = DependencyProperty.Register(
        nameof(DismissCommandParameter), typeof(object), typeof(Banner), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(Banner), new FrameworkPropertyMetadata(new CornerRadius(6)));

    static Banner()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(Banner), new FrameworkPropertyMetadata(typeof(Banner)));
        FocusableProperty.OverrideMetadata(typeof(Banner), new FrameworkPropertyMetadata(false));
    }

    public BannerKind Kind
    {
        get => (BannerKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>Overrides the kind's icon (e.g. CloudOff for "Offline").</summary>
    public Geometry? Icon
    {
        get => (Geometry?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>The one action ("Resolve", "Retry"); hidden when null.</summary>
    public object? ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }

    public ICommand? ActionCommand
    {
        get => (ICommand?)GetValue(ActionCommandProperty);
        set => SetValue(ActionCommandProperty, value);
    }

    public object? ActionCommandParameter
    {
        get => GetValue(ActionCommandParameterProperty);
        set => SetValue(ActionCommandParameterProperty, value);
    }

    /// <summary>Shows a dismiss (X) button that runs this command; hidden when null.</summary>
    public ICommand? DismissCommand
    {
        get => (ICommand?)GetValue(DismissCommandProperty);
        set => SetValue(DismissCommandProperty, value);
    }

    public object? DismissCommandParameter
    {
        get => GetValue(DismissCommandParameterProperty);
        set => SetValue(DismissCommandParameterProperty, value);
    }

    /// <summary>6 by default; set 0 for a full-bleed banner under the tab strip.</summary>
    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }
}
