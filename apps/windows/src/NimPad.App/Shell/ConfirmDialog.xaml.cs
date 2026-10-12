using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using NimPad.Presentation.Platform;

namespace NimPad.App.Shell;

/// <summary>
/// The confirmation dialog of <see cref="Platform.DialogService"/> (design pages 25 and 27): focus starts on Cancel (the
/// safe action), Tab cycles inside the dialog, Enter confirms the primary button and Esc cancels.
/// </summary>
public partial class ConfirmDialog : IModalDialog
{
    /// <summary>Builds the dialog for <paramref name="request"/>.</summary>
    public ConfirmDialog(ConfirmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        InitializeComponent();
        Request = request;
        TitleText.Text = request.Title;
        MessageText.Text = request.Message;
        MessageText.Visibility = string.IsNullOrWhiteSpace(request.Message) ? Visibility.Collapsed : Visibility.Visible;
        PrimaryButton.Content = request.PrimaryText;
        CancelButton.Content = request.CancelText;
        if (request.IsDestructive)
        {
            PrimaryButton.SetResourceReference(StyleProperty, "DestructiveButton");
        }

        if (request.SecondaryText is { } secondary)
        {
            SecondaryButton.Content = secondary;
        }
        else
        {
            SecondaryButton.Visibility = Visibility.Collapsed;
        }

        AutomationProperties.SetName(this, request.Title);
        AutomationProperties.SetHelpText(this, request.Message);
    }

    /// <inheritdoc/>
    public event EventHandler? Finished;

    /// <summary>What the dialog shows.</summary>
    public ConfirmRequest Request { get; }

    /// <summary>The choice; <see cref="DialogChoice.Cancel"/> until a button is used.</summary>
    public DialogChoice Choice { get; private set; } = DialogChoice.Cancel;

    /// <inheritdoc/>
    public bool CancelOnScrimClick => false;

    /// <inheritdoc/>
    public void FocusInitialElement()
    {
        CancelButton.Focus();
        Keyboard.Focus(CancelButton);
    }

    /// <inheritdoc/>
    public void Cancel() => Finish(DialogChoice.Cancel);

    /// <inheritdoc/>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!e.Handled && e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            // Enter confirms the primary button wherever focus is in the dialog.
            e.Handled = true;
            Finish(DialogChoice.Primary);
        }
    }

    private void Finish(DialogChoice choice)
    {
        if (Finished is null)
        {
            return;
        }

        Choice = choice;
        var finished = Finished;
        Finished = null;
        finished(this, EventArgs.Empty);
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e) => Finish(DialogChoice.Primary);

    private void OnSecondaryClick(object sender, RoutedEventArgs e) => Finish(DialogChoice.Secondary);

    private void OnCancelClick(object sender, RoutedEventArgs e) => Finish(DialogChoice.Cancel);
}
