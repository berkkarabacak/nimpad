using System.Globalization;
using System.Windows;
using System.Windows.Data;
using NimPad.Presentation.Feedback;
using NimPad.Presentation.Tabs;
using ControlsBannerKind = NimPad.App.Controls.BannerKind;

namespace NimPad.App.Shell;

/// <summary>Value converters used by the shell's XAML (<c>{x:Static shell:ShellConverters.…}</c>).</summary>
public static class ShellConverters
{
    /// <summary><see langword="true"/> → Visible, otherwise Collapsed.</summary>
    public static IValueConverter TrueToVisible { get; } = new BoolToVisibilityConverter(visibleWhen: true);

    /// <summary><see langword="false"/> → Visible, otherwise Collapsed.</summary>
    public static IValueConverter FalseToVisible { get; } = new BoolToVisibilityConverter(visibleWhen: false);

    /// <summary>Non-null (and non-empty text) → Visible, otherwise Collapsed.</summary>
    public static IValueConverter NotEmptyToVisible { get; } = new NotEmptyToVisibilityConverter();

    /// <summary>Non-null (and non-empty text) → <see langword="true"/>.</summary>
    public static IValueConverter NotEmpty { get; } = new NotEmptyConverter();

    /// <summary>Negates a <see cref="bool"/>.</summary>
    public static IValueConverter Not { get; } = new NotConverter();

    /// <summary>A tab's <see cref="TabViewModel.SaveState"/> and <see cref="TabViewModel.IsDirty"/> → its UI Automation ItemStatus.</summary>
    public static IMultiValueConverter TabItemStatus { get; } = new TabItemStatusConverter();

    /// <summary>True when every value is <see langword="true"/>.</summary>
    public static IMultiValueConverter AllTrue { get; } = new AllTrueConverter();

    /// <summary>Visible when every value is <see langword="true"/>, otherwise Collapsed.</summary>
    public static IMultiValueConverter AllTrueToVisible { get; } = new AllTrueToVisibleConverter();

    /// <summary>
    /// The command (second value) when the flag (first value) is <see langword="true"/>, otherwise <see langword="null"/>
    /// (a banner shows its dismiss button only when it can be dismissed).
    /// </summary>
    public static IMultiValueConverter CommandWhen { get; } = new CommandWhenConverter();

    /// <summary>True when both values are equal (a mode menu item and the active mode).</summary>
    public static IMultiValueConverter AreEqual { get; } = new AreEqualConverter();

    /// <summary>The view model's <see cref="BannerKind"/> → the banner control's kind.</summary>
    public static IValueConverter BannerKindToControl { get; } = new BannerKindConverter();

    /// <summary>The UI Automation ItemStatus of a tab: <c>error</c>, <c>unsaved</c> or <c>saved</c>.</summary>
    public static string ItemStatusOf(TabSaveState saveState, bool isDirty) => saveState switch
    {
        TabSaveState.Failed => "error",
        TabSaveState.Pending => "unsaved",
        _ when isDirty => "unsaved",
        _ => "saved",
    };

    private sealed class BoolToVisibilityConverter(bool visibleWhen) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is bool flag && flag == visibleWhen ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is Visibility visibility ? (visibility == Visibility.Visible) == visibleWhen : DependencyProperty.UnsetValue;
    }

    private sealed class NotEmptyToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
        {
            null => Visibility.Collapsed,
            string text when string.IsNullOrWhiteSpace(text) => Visibility.Collapsed,
            _ => Visibility.Visible,
        };

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
    }

    private sealed class NotEmptyConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is string text ? !string.IsNullOrWhiteSpace(text) : value is not null;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
    }

    private sealed class NotConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool flag ? !flag : DependencyProperty.UnsetValue;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool flag ? !flag : DependencyProperty.UnsetValue;
    }

    private sealed class TabItemStatusConverter : IMultiValueConverter
    {
        public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
            values is [TabSaveState state, bool dirty, ..] ? ItemStatusOf(state, dirty) : string.Empty;

        public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class AllTrueConverter : IMultiValueConverter
    {
        public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
            values.Length > 0 && values.All(value => value is true);

        public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class AllTrueToVisibleConverter : IMultiValueConverter
    {
        public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
            values.Length > 0 && values.All(value => value is true) ? Visibility.Visible : Visibility.Collapsed;

        public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class CommandWhenConverter : IMultiValueConverter
    {
        public object? Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
            values is [true, System.Windows.Input.ICommand command, ..] ? command : null;

        public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class AreEqualConverter : IMultiValueConverter
    {
        public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
            values.Length >= 2 && Equals(values[0], values[1]);

        public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class BannerKindConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
        {
            BannerKind.Warning => ControlsBannerKind.Warning,
            BannerKind.Error => ControlsBannerKind.Error,
            _ => ControlsBannerKind.Info,
        };

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
    }
}
