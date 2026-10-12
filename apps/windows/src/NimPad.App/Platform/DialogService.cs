using NimPad.App.Services;
using NimPad.App.Shell;
using NimPad.Presentation.Platform;

namespace NimPad.App.Platform;

/// <summary>
/// <see cref="IDialogService"/> over the main window's <see cref="ModalLayer"/>: the design's dialog card over a scrim
/// (design page 25), inside the window. Requests made while another dialog is open wait their turn. Before the main
/// window attached its layer, a request is answered with Cancel (nothing is decided without the user).
/// </summary>
public sealed class DialogService : IDialogService
{
    private ModalLayer? _layer;

    /// <summary>Connects the layer the dialogs appear on (the main window does this when it is created).</summary>
    public void Attach(ModalLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        _layer = layer;
    }

    /// <inheritdoc/>
    public async Task<bool> ConfirmAsync(ConfirmRequest request) =>
        await ChooseAsync(request with { SecondaryText = null }) == DialogChoice.Primary;

    /// <inheritdoc/>
    public async Task<DialogChoice> ChooseAsync(ConfirmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_layer is not { } layer)
        {
            FileLog.Warn($"A dialog was requested before the window was ready and was cancelled: {request.Title}");
            return DialogChoice.Cancel;
        }

        var dialog = new ConfirmDialog(request);
        await layer.ShowAsync(dialog);
        return dialog.Choice;
    }
}
