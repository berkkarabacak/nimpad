using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace NimPad.App.Shell;

/// <summary>
/// A toggle button whose checked state belongs to the view model: toggling (click, Space, or the UI Automation Toggle
/// pattern) runs <see cref="ToggleCommand"/> instead of flipping <c>IsChecked</c>, so a one-way binding to the state
/// (for example the sidebar being open) stays intact and is the only source of truth.
/// </summary>
public class CommandToggleButton : ToggleButton
{
    /// <summary>The <see cref="ToggleCommand"/> property.</summary>
    public static readonly DependencyProperty ToggleCommandProperty = DependencyProperty.Register(
        nameof(ToggleCommand), typeof(ICommand), typeof(CommandToggleButton), new FrameworkPropertyMetadata(null));

    /// <summary>The <see cref="ToggleCommandParameter"/> property.</summary>
    public static readonly DependencyProperty ToggleCommandParameterProperty = DependencyProperty.Register(
        nameof(ToggleCommandParameter), typeof(object), typeof(CommandToggleButton), new FrameworkPropertyMetadata(null));

    /// <summary>Runs when the button is toggled; the view model then changes the bound state.</summary>
    public ICommand? ToggleCommand
    {
        get => (ICommand?)GetValue(ToggleCommandProperty);
        set => SetValue(ToggleCommandProperty, value);
    }

    /// <summary>The parameter of <see cref="ToggleCommand"/>.</summary>
    public object? ToggleCommandParameter
    {
        get => GetValue(ToggleCommandParameterProperty);
        set => SetValue(ToggleCommandParameterProperty, value);
    }

    /// <inheritdoc/>
    protected override void OnToggle()
    {
        var parameter = ToggleCommandParameter;
        if (ToggleCommand is { } command && command.CanExecute(parameter))
        {
            command.Execute(parameter);
        }
    }
}
