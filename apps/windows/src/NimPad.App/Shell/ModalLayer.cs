using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;
using NimPad.App.Services;

namespace NimPad.App.Shell;

/// <summary>A dialog shown by <see cref="ModalLayer"/>.</summary>
public interface IModalDialog
{
    /// <summary>Raised when the dialog is finished (its result, if any, is on the dialog).</summary>
    event EventHandler? Finished;

    /// <summary>Whether a click on the scrim cancels the dialog.</summary>
    bool CancelOnScrimClick { get; }

    /// <summary>Moves keyboard focus to the dialog's initial element (the safe action, or the filter field).</summary>
    void FocusInitialElement();

    /// <summary>Cancels the dialog (Esc, scrim click): it raises <see cref="Finished"/> with its cancel result.</summary>
    void Cancel();
}

/// <summary>
/// The window's modal layer (design pages 12 and 25): a scrim over the whole window with one dialog card centered on
/// it. While a dialog is shown, keyboard focus stays inside it (Tab cycles, the window's shortcuts do not run, Esc
/// cancels) and the scrim takes every click, including on the title bar. Requests made while a dialog is open wait their turn. When the dialog closes, keyboard
/// focus returns to where it was.
/// </summary>
public sealed class ModalLayer : Grid
{
    private readonly Border _scrim;
    private Task _queue = Task.CompletedTask;
    private IModalDialog? _current;
    private IInputElement? _restoreFocus;

    /// <summary>Creates the (hidden) layer.</summary>
    public ModalLayer()
    {
        Visibility = Visibility.Collapsed;
        _scrim = new Border();
        _scrim.SetResourceReference(Border.BackgroundProperty, "ScrimBrush");
        _scrim.MouseLeftButtonDown += OnScrimMouseDown;
        Children.Add(_scrim);
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(this, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(this, KeyboardNavigationMode.Contained);
        Focusable = false;

        // The scrim also covers the title bar: it takes those clicks instead of letting them move the window.
        WindowChrome.SetIsHitTestVisibleInChrome(this, true);
    }

    /// <summary>True while a dialog is shown.</summary>
    public bool IsOpen => _current is not null;

    /// <summary>The dialog shown now, if any.</summary>
    public IModalDialog? Current => _current;

    /// <summary>Raised when a dialog opens or closes.</summary>
    public event EventHandler? IsOpenChanged;

    /// <summary>Tells whether a key is one of the window's shortcuts (those are swallowed while a dialog is open).</summary>
    public Func<KeyEventArgs, bool>? IsWindowShortcut { get; set; }

    /// <summary>
    /// Called after a dialog closed when the element that had focus before it can no longer take it (for example the
    /// tab it belonged to was closed); the window focuses the editor.
    /// </summary>
    public Action? FocusFallback { get; set; }

    /// <summary>Shows <paramref name="dialog"/> (after any dialog already shown) and completes when it is finished.</summary>
    public Task ShowAsync(FrameworkElement dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        if (dialog is not IModalDialog)
        {
            throw new ArgumentException("The dialog must implement IModalDialog.", nameof(dialog));
        }

        var previous = _queue;
        var shown = ShowAfterAsync(previous, dialog);
        _queue = shown;
        return shown;
    }

    /// <summary>Cancels the dialog shown now, if any.</summary>
    public void CancelCurrent() => _current?.Cancel();

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_current is null || e.Handled)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            _current.Cancel();
            e.Handled = true;
            return;
        }

        // The window's shortcuts (Ctrl+N, F6, …) must not act behind the dialog. Only those keys are stopped: a handled
        // key down produces no character, so typing in the dialog (and AltGr) keeps working.
        if (IsWindowShortcut?.Invoke(e) == true)
        {
            e.Handled = true;
        }
    }

    private async Task ShowAfterAsync(Task previous, FrameworkElement element)
    {
        try
        {
            await previous;
        }
        catch (Exception ex)
        {
            FileLog.Warn("An earlier dialog failed.", ex);
        }

        var dialog = (IModalDialog)element;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFinished(object? sender, EventArgs e) => finished.TrySetResult();
        dialog.Finished += OnFinished;
        _restoreFocus = Keyboard.FocusedElement;
        _current = dialog;
        element.HorizontalAlignment = HorizontalAlignment.Center;
        element.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(element);
        Visibility = Visibility.Visible;
        IsOpenChanged?.Invoke(this, EventArgs.Empty);
        if (element.IsLoaded)
        {
            dialog.FocusInitialElement();
        }
        else
        {
            element.Loaded += FocusWhenLoaded;
        }

        try
        {
            await finished.Task;
        }
        finally
        {
            dialog.Finished -= OnFinished;
            element.Loaded -= FocusWhenLoaded;
            Children.Remove(element);
            _current = null;
            Visibility = Visibility.Collapsed;
            IsOpenChanged?.Invoke(this, EventArgs.Empty);
            RestoreFocus();
        }

        void FocusWhenLoaded(object sender, RoutedEventArgs e)
        {
            element.Loaded -= FocusWhenLoaded;
            dialog.FocusInitialElement();
        }
    }

    private void RestoreFocus()
    {
        var target = _restoreFocus;
        _restoreFocus = null;
        if (target is UIElement { IsVisible: true, IsEnabled: true, Focusable: true } element
            && PresentationSource.FromVisual(element) is not null
            && Keyboard.Focus(element) == element)
        {
            return;
        }

        if (target is ContentElement { IsEnabled: true, Focusable: true } content && Keyboard.Focus(content) == content)
        {
            return;
        }

        FocusFallback?.Invoke();
    }

    private void OnScrimMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_current is { CancelOnScrimClick: true } dialog)
        {
            dialog.Cancel();
        }
    }
}
