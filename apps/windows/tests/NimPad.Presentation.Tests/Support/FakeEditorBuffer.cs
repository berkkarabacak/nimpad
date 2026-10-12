using NimPad.Presentation.Platform;

namespace NimPad.Presentation.Tests.Support;

/// <summary>An editor document for tests: a string with a version that grows on every edit (thread-safe).</summary>
internal sealed class FakeEditorBuffer : IEditorBuffer
{
    private readonly Lock _gate = new();
    private string _text;
    private long _version;

    public FakeEditorBuffer(string text = "")
    {
        _text = text;
    }

    public long Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    public int TextLength
    {
        get
        {
            lock (_gate)
            {
                return _text.Length;
            }
        }
    }

    public string Text => GetSnapshotText();

    public int SnapshotCount { get; private set; }

    public string GetSnapshotText()
    {
        lock (_gate)
        {
            SnapshotCount++;
            return _text;
        }
    }

    public void SetText(string text)
    {
        lock (_gate)
        {
            _text = text;
            _version++;
        }
    }

    public void Append(string text)
    {
        lock (_gate)
        {
            _text += text;
            _version++;
        }
    }

    /// <summary>Applies edits whose offsets refer to the current text (from last to first, as one change).</summary>
    public void Apply(IReadOnlyList<NimPad.Presentation.Find.TextEdit> edits)
    {
        lock (_gate)
        {
            // Linear in the text and the edit count (a Replace all can bring 100,000 edits).
            var text = _text;
            var result = new System.Text.StringBuilder(text.Length);
            var position = 0;
            foreach (var edit in edits.OrderBy(edit => edit.Offset))
            {
                result.Append(text, position, edit.Offset - position).Append(edit.Text);
                position = edit.Offset + edit.Length;
            }

            result.Append(text, position, text.Length - position);
            _text = result.ToString();
            _version++;
        }
    }
}
