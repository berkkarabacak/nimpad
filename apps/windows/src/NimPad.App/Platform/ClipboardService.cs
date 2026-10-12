using System.Windows;
using System.Windows.Threading;
using NimPad.Presentation.Platform;

namespace NimPad.App.Platform;

/// <summary>
/// <see cref="IClipboardService"/> over WPF's <see cref="Clipboard"/> (which retries briefly while another app holds
/// the clipboard). A clipboard that stays busy throws, and the caller shows an error toast.
/// </summary>
public sealed class ClipboardService : IClipboardService
{
    private readonly Dispatcher _dispatcher;

    /// <summary>Uses the clipboard from the UI thread of <paramref name="dispatcher"/> (it needs an STA thread).</summary>
    public ClipboardService(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }

    /// <inheritdoc/>
    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_dispatcher.CheckAccess())
        {
            Clipboard.SetText(text);
        }
        else
        {
            _dispatcher.Invoke(() => Clipboard.SetText(text));
        }
    }
}
