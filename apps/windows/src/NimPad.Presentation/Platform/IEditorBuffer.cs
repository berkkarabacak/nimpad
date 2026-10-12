namespace NimPad.Presentation.Platform;

/// <summary>
/// The live text of one open tab. The App wraps AvalonEdit's <c>TextDocument</c> and hands it to
/// <see cref="Tabs.TabViewModel.AttachBuffer"/> once the tab's text is loaded; afterwards it calls
/// <see cref="Tabs.TabViewModel.OnBufferChanged"/> on every change of the document.
/// </summary>
/// <remarks>
/// This is the source the Core <c>AutosaveScheduler</c> saves from (the tab adapts it to <c>IAutosaveSource</c>), so
/// its contract applies: <see cref="Version"/> only grows and is safe to read on any thread, and
/// <see cref="GetSnapshotText"/> may be called from any thread (a timer thread or the thread that flushes); the App
/// takes the snapshot on the UI thread (<c>TextDocument.CreateSnapshot().Text</c>), marshaling when it is called
/// elsewhere.
/// </remarks>
public interface IEditorBuffer
{
    /// <summary>
    /// A counter that increases with every change of the text (never decreases); safe to read from any thread.
    /// AvalonEdit: a counter incremented in <c>TextDocument.Changed</c>.
    /// </summary>
    long Version { get; }

    /// <summary>The current length of the text, in UTF-16 code units (cheap; never materializes the text).</summary>
    int TextLength { get; }

    /// <summary>The current text. Thread-safe: the App snapshots on the UI thread.</summary>
    string GetSnapshotText();
}
