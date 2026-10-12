using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NimPad.App.Controls;

/// <summary>
/// Attached properties understood by the NimPad control styles (Themes/Controls.*.xaml).
/// XAML: <c>xmlns:c="clr-namespace:NimPad.App.Controls"</c>, then e.g. <c>c:Ui.Icon="{StaticResource Trash}"</c>.
/// </summary>
public static class Ui
{
    /// <summary>
    /// Icon geometry (24-grid, see Themes/Icons.xaml) drawn before the content of buttons, pills, menu items and
    /// toggle buttons. A button with an icon and no content renders the icon alone.
    /// </summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(Geometry), typeof(Ui), new FrameworkPropertyMetadata(null));

    /// <summary>Size of <see cref="IconProperty"/> in DIPs (default 16).</summary>
    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.RegisterAttached(
        "IconSize", typeof(double), typeof(Ui), new FrameworkPropertyMetadata(16.0));

    /// <summary>Stroke width of <see cref="IconProperty"/> in DIPs (default 1.25; title bar glyphs use 1).</summary>
    public static readonly DependencyProperty IconStrokeThicknessProperty = DependencyProperty.RegisterAttached(
        "IconStrokeThickness", typeof(double), typeof(Ui), new FrameworkPropertyMetadata(1.25));

    /// <summary>Corner radius used by button-like templates (the styles set 4, 6 or full).</summary>
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(Ui), new FrameworkPropertyMetadata(new CornerRadius(6)));

    /// <summary>Placeholder text shown by the TextBox and ComboBox styles while they are empty.</summary>
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null));

    /// <summary>Puts a TextBox into its error state (Err border, <see cref="ErrorTextProperty"/> underneath).</summary>
    public static readonly DependencyProperty HasErrorProperty = DependencyProperty.RegisterAttached(
        "HasError", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false));

    /// <summary>Message shown under a TextBox in Err color while <see cref="HasErrorProperty"/> is true.</summary>
    public static readonly DependencyProperty ErrorTextProperty = DependencyProperty.RegisterAttached(
        "ErrorText", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null));

    /// <summary>
    /// Inherited flag for the inactive-window look (title bar glyphs and text dim to Text3). Set it once on the
    /// window: <c>c:Ui.IsWindowActive="{Binding IsActive, RelativeSource={RelativeSource Self}}"</c>.
    /// </summary>
    public static readonly DependencyProperty IsWindowActiveProperty = DependencyProperty.RegisterAttached(
        "IsWindowActive", typeof(bool), typeof(Ui),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits));

    /// <summary>
    /// Shows the hover look without a mouse over the element. Used for the Snap Layouts maximize button (which
    /// receives WM_NCMOUSEMOVE instead of WPF mouse events) and by the design gallery.
    /// </summary>
    public static readonly DependencyProperty ForceHoverProperty = DependencyProperty.RegisterAttached(
        "ForceHover", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false));

    /// <summary>Shows the pressed look without a mouse press (see <see cref="ForceHoverProperty"/>).</summary>
    public static readonly DependencyProperty ForcePressedProperty = DependencyProperty.RegisterAttached(
        "ForcePressed", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false));

    /// <summary>
    /// Shows the in-template focus ring of text inputs (TextBox, SearchBox, ComboBox) without keyboard focus.
    /// For state previews such as the design gallery.
    /// </summary>
    public static readonly DependencyProperty ForceFocusedProperty = DependencyProperty.RegisterAttached(
        "ForceFocused", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false));

    /// <summary>Where the ToggleSwitch style places its content (Right, the default, or Left).</summary>
    public static readonly DependencyProperty LabelPlacementProperty = DependencyProperty.RegisterAttached(
        "LabelPlacement", typeof(Dock), typeof(Ui), new FrameworkPropertyMetadata(Dock.Right));

    public static Geometry? GetIcon(DependencyObject element) => (Geometry?)element.GetValue(IconProperty);

    public static void SetIcon(DependencyObject element, Geometry? value) => element.SetValue(IconProperty, value);

    public static double GetIconSize(DependencyObject element) => (double)element.GetValue(IconSizeProperty);

    public static void SetIconSize(DependencyObject element, double value) => element.SetValue(IconSizeProperty, value);

    public static double GetIconStrokeThickness(DependencyObject element) =>
        (double)element.GetValue(IconStrokeThicknessProperty);

    public static void SetIconStrokeThickness(DependencyObject element, double value) =>
        element.SetValue(IconStrokeThicknessProperty, value);

    public static bool GetForceFocused(DependencyObject element) => (bool)element.GetValue(ForceFocusedProperty);

    public static void SetForceFocused(DependencyObject element, bool value) =>
        element.SetValue(ForceFocusedProperty, value);

    public static CornerRadius GetCornerRadius(DependencyObject element) =>
        (CornerRadius)element.GetValue(CornerRadiusProperty);

    public static void SetCornerRadius(DependencyObject element, CornerRadius value) =>
        element.SetValue(CornerRadiusProperty, value);

    public static string? GetPlaceholder(DependencyObject element) => (string?)element.GetValue(PlaceholderProperty);

    public static void SetPlaceholder(DependencyObject element, string? value) =>
        element.SetValue(PlaceholderProperty, value);

    public static bool GetHasError(DependencyObject element) => (bool)element.GetValue(HasErrorProperty);

    public static void SetHasError(DependencyObject element, bool value) => element.SetValue(HasErrorProperty, value);

    public static string? GetErrorText(DependencyObject element) => (string?)element.GetValue(ErrorTextProperty);

    public static void SetErrorText(DependencyObject element, string? value) =>
        element.SetValue(ErrorTextProperty, value);

    public static bool GetIsWindowActive(DependencyObject element) => (bool)element.GetValue(IsWindowActiveProperty);

    public static void SetIsWindowActive(DependencyObject element, bool value) =>
        element.SetValue(IsWindowActiveProperty, value);

    public static bool GetForceHover(DependencyObject element) => (bool)element.GetValue(ForceHoverProperty);

    public static void SetForceHover(DependencyObject element, bool value) =>
        element.SetValue(ForceHoverProperty, value);

    public static bool GetForcePressed(DependencyObject element) => (bool)element.GetValue(ForcePressedProperty);

    public static void SetForcePressed(DependencyObject element, bool value) =>
        element.SetValue(ForcePressedProperty, value);

    public static Dock GetLabelPlacement(DependencyObject element) => (Dock)element.GetValue(LabelPlacementProperty);

    public static void SetLabelPlacement(DependencyObject element, Dock value) =>
        element.SetValue(LabelPlacementProperty, value);
}
