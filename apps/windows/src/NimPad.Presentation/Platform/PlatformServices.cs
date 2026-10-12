namespace NimPad.Presentation.Platform;

/// <summary>Puts text on the system clipboard.</summary>
public interface IClipboardService
{
    /// <summary>Replaces the clipboard content with <paramref name="text"/>.</summary>
    void SetText(string text);
}

/// <summary>The system file pickers.</summary>
public interface IFileDialogService
{
    /// <summary>Shows the Open dialog (multi-select); empty when the user cancelled.</summary>
    IReadOnlyList<string> PickFilesToOpen();

    /// <summary>Shows the Save dialog.</summary>
    /// <param name="suggestedName">The file name to prefill, for example <c>Meeting notes.md</c>.</param>
    /// <param name="initialDirectory">The folder to start in, or <see langword="null"/> for the system default.</param>
    /// <returns>The chosen full path, or <see langword="null"/> when the user cancelled.</returns>
    string? PickSaveFile(string suggestedName, string? initialDirectory);
}

/// <summary>Which button closed a confirmation dialog.</summary>
public enum DialogChoice
{
    /// <summary>The primary button (for example "Save" or "Delete forever").</summary>
    Primary,

    /// <summary>The secondary button (for example "Don't save"), when the request has one.</summary>
    Secondary,

    /// <summary>Cancel, Esc or the close button.</summary>
    Cancel,
}

/// <summary>
/// What a confirmation dialog shows. The design's dialogs open with focus on the safe action (Cancel, or the primary
/// button when it is not destructive), Enter confirms the focused button and Esc cancels.
/// </summary>
/// <param name="Title">The dialog title, for example <c>Delete “Old agenda” forever?</c>.</param>
/// <param name="Message">One or two plain sentences.</param>
/// <param name="PrimaryText">The primary button, for example <c>Delete forever</c>.</param>
/// <param name="IsDestructive">Styles the primary button as destructive and focuses Cancel.</param>
/// <param name="CancelText">The cancel button text.</param>
/// <param name="SecondaryText">A middle button (for example <c>Don't save</c>), or <see langword="null"/> for none.</param>
public sealed record ConfirmRequest(
    string Title,
    string Message,
    string PrimaryText,
    bool IsDestructive = false,
    string CancelText = "Cancel",
    string? SecondaryText = null);

/// <summary>Modal dialogs over the main window.</summary>
public interface IDialogService
{
    /// <summary>Shows a two-button confirmation; true when the primary button was chosen.</summary>
    Task<bool> ConfirmAsync(ConfirmRequest request);

    /// <summary>
    /// Shows a three-way choice (primary, <see cref="ConfirmRequest.SecondaryText"/>, cancel), such as "Save changes to
    /// “notes.txt”?" with Save / Don't save / Cancel.
    /// </summary>
    Task<DialogChoice> ChooseAsync(ConfirmRequest request);
}

/// <summary>Whether Windows asks apps to use dark mode.</summary>
public interface ISystemThemeProvider
{
    /// <summary>True when Windows' app mode is dark.</summary>
    bool IsDarkMode { get; }

    /// <summary>Raised (on the UI thread) when <see cref="IsDarkMode"/> changes.</summary>
    event EventHandler? Changed;
}

/// <summary>Starting NimPad when the user signs in to Windows (HKCU Run key).</summary>
public interface IAutostartService
{
    /// <summary>Whether NimPad currently starts with Windows.</summary>
    bool IsEnabled { get; }

    /// <summary>Turns starting with Windows on or off.</summary>
    void Set(bool enabled);
}

/// <summary>The system-wide shortcut that brings NimPad forward (<c>RegisterHotKey</c>).</summary>
public interface IGlobalHotkeyService
{
    /// <summary>
    /// Registers <paramref name="gesture"/> (normalized form such as <c>Ctrl+Alt+N</c>), replacing the previous
    /// registration. When it fails, the previous registration stays in effect.
    /// </summary>
    /// <param name="gesture">The shortcut.</param>
    /// <param name="error">When registration fails, a user-facing reason (for example another app uses it).</param>
    /// <returns><see langword="true"/> when the shortcut is registered.</returns>
    bool TryRegister(string gesture, out string? error);

    /// <summary>Removes the registration, if any.</summary>
    void Unregister();
}

/// <summary>The installed fonts that suit the editor.</summary>
public interface IFontCatalog
{
    /// <summary>Monospace font family names, sorted, including the bundled Cascadia Mono.</summary>
    IReadOnlyList<string> MonospaceFamilies { get; }
}

/// <summary>Opens links and folders with the system shell.</summary>
public interface IShellLauncher
{
    /// <summary>Opens <paramref name="uri"/> (a web link, or a <c>file:</c> URI of a folder) in its default app.</summary>
    void OpenUri(Uri uri);
}

/// <summary>The platform services the App supplies to the view models.</summary>
public sealed class PlatformServices
{
    /// <summary>The UI thread.</summary>
    public required IUiDispatcher Dispatcher { get; init; }

    /// <summary>The clipboard.</summary>
    public required IClipboardService Clipboard { get; init; }

    /// <summary>The Open and Save dialogs.</summary>
    public required IFileDialogService FileDialogs { get; init; }

    /// <summary>Confirmation dialogs.</summary>
    public required IDialogService Dialogs { get; init; }

    /// <summary>Windows' light or dark app mode.</summary>
    public required ISystemThemeProvider SystemTheme { get; init; }

    /// <summary>Start with Windows.</summary>
    public required IAutostartService Autostart { get; init; }

    /// <summary>The global shortcut.</summary>
    public required IGlobalHotkeyService GlobalHotkey { get; init; }

    /// <summary>Editor fonts.</summary>
    public required IFontCatalog Fonts { get; init; }

    /// <summary>Links and folders.</summary>
    public required IShellLauncher Shell { get; init; }
}
