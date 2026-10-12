using ICSharpCode.AvalonEdit.Document;
using NimPad.Presentation.Platform;

namespace NimPad.App.Views.Editor;

/// <summary>
/// <see cref="IEditorBuffer"/> over one tab's AvalonEdit <see cref="TextDocument"/>. <see cref="Version"/> counts the
/// document's changes (<see cref="OnDocumentChanged"/>, called on the UI thread from <c>TextDocument.Changed</c>) and is
/// read with <see cref="Interlocked"/>; <see cref="TextLength"/> is cached on the same change, so both are safe on any
/// thread. <see cref="GetSnapshotText"/> uses <see cref="TextDocument.CreateSnapshot()"/>, the one TextDocument method
/// AvalonEdit documents as safe to call from any thread while the owner thread edits, so autosave timers and the
/// shutdown flush never wait for the UI thread (which may itself be waiting for them in <c>EmergencyFlush</c>).
/// </summary>
public sealed class EditorBuffer : IEditorBuffer
{
    private readonly TextDocument _document;
    private long _version;
    private int _textLength;

    /// <summary>Wraps <paramref name="document"/> (create it on the UI thread).</summary>
    public EditorBuffer(TextDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _document = document;
        _textLength = document.TextLength;
    }

    /// <summary>The wrapped document.</summary>
    public TextDocument Document => _document;

    /// <inheritdoc/>
    public long Version => Interlocked.Read(ref _version);

    /// <inheritdoc/>
    public int TextLength => Volatile.Read(ref _textLength);

    /// <inheritdoc/>
    public string GetSnapshotText() => _document.CreateSnapshot().Text;

    /// <summary>Records a change of the document (call it from <c>TextDocument.Changed</c>, before telling the tab).</summary>
    public void OnDocumentChanged()
    {
        Volatile.Write(ref _textLength, _document.TextLength);
        Interlocked.Increment(ref _version);
    }
}
