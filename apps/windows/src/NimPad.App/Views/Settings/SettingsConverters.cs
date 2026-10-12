using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NimPad.App.Views.Settings;

/// <summary>
/// <c>true</c> → the text before the bar of the parameter, <c>false</c> → the text after it:
/// <c>ConverterParameter='On|Off'</c>.
/// </summary>
public sealed class BooleanToTextConverter : IValueConverter
{
    /// <summary>The shared instance.</summary>
    public static BooleanToTextConverter Instance { get; } = new();

    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var texts = (parameter as string ?? "True|False").Split('|');
        return value is true ? texts[0] : texts.Length > 1 ? texts[1] : string.Empty;
    }

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// Visible when the value is <c>true</c> (or, with <c>ConverterParameter=Invert</c>, when it is <c>false</c>); a
/// non-empty string counts as <c>true</c>.
/// </summary>
public sealed class VisibleWhenConverter : IValueConverter
{
    /// <summary>The shared instance.</summary>
    public static VisibleWhenConverter Instance { get; } = new();

    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var on = value switch
        {
            bool flag => flag,
            string text => !string.IsNullOrEmpty(text),
            null => false,
            _ => true,
        };
        if (parameter is "Invert")
        {
            on = !on;
        }

        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible when the value equals the parameter (the page of the selected section).</summary>
public sealed class VisibleWhenEqualConverter : IValueConverter
{
    /// <summary>The shared instance.</summary>
    public static VisibleWhenEqualConverter Instance { get; } = new();

    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value, parameter) ? Visibility.Visible : Visibility.Collapsed;

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary><c>true</c> → <see cref="TextWrapping.Wrap"/>, otherwise <see cref="TextWrapping.NoWrap"/> (the preview follows Word wrap).</summary>
public sealed class WrapConverter : IValueConverter
{
    /// <summary>The shared instance.</summary>
    public static WrapConverter Instance { get; } = new();

    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? TextWrapping.Wrap : TextWrapping.NoWrap;

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
