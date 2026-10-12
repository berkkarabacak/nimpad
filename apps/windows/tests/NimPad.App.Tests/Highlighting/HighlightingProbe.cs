using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using Xunit;

namespace NimPad.App.Tests.Highlighting;

/// <summary>Runs AvalonEdit's <see cref="DocumentHighlighter"/> over a sample and reports what a token is painted with.</summary>
internal sealed class HighlightingProbe : IDisposable
{
    private readonly string _text;
    private readonly TextDocument _document;
    private readonly DocumentHighlighter _highlighter;

    public HighlightingProbe(IHighlightingDefinition definition, string text)
    {
        _text = text;
        _document = new TextDocument(text);
        _highlighter = new DocumentHighlighter(_document, definition);
    }

    /// <summary>
    /// The effective foreground of every character of the <paramref name="occurrence"/>-th <paramref name="token"/>
    /// (null = the editor's default text color), asserting that the whole token has one color.
    /// </summary>
    public Color? ForegroundOf(string token, int occurrence = 1) => Uniform(token, occurrence, ForegroundAt);

    /// <summary>The effective font weight of the token (null = the editor's default weight).</summary>
    public FontWeight? FontWeightOf(string token, int occurrence = 1) => Uniform(token, occurrence, FontWeightAt);

    /// <summary>The effective font style of the token (null = the editor's default style).</summary>
    public FontStyle? FontStyleOf(string token, int occurrence = 1) => Uniform(token, occurrence, FontStyleAt);

    public void Dispose() => _highlighter.Dispose();

    private T? Uniform<T>(string token, int occurrence, Func<HighlightedLine, int, T?> probe)
        where T : struct
    {
        var offset = Find(token, occurrence);
        var line = _highlighter.HighlightLine(_document.GetLineByOffset(offset).LineNumber);
        var first = probe(line, offset);
        for (var i = 1; i < token.Length; i++)
        {
            var other = probe(line, offset + i);
            Assert.True(Equals(first, other), $"'{token}' is not uniform: '{token[0]}' is {first}, '{token[i]}' is {other}.");
        }

        return first;
    }

    private int Find(string token, int occurrence)
    {
        var offset = -1;
        for (var i = 0; i < occurrence; i++)
        {
            offset = _text.IndexOf(token, offset + 1, StringComparison.Ordinal);
            Assert.True(offset >= 0, $"'{token}' (occurrence {occurrence}) is not in the sample.");
        }

        return offset;
    }

    // Sections are ordered so that nested sections follow their parents: the last match is the innermost one.
    private static Color? ForegroundAt(HighlightedLine line, int offset) =>
        Innermost(line, offset, color => color.Foreground?.GetColor(null));

    private static FontWeight? FontWeightAt(HighlightedLine line, int offset) =>
        Innermost(line, offset, color => color.FontWeight);

    private static FontStyle? FontStyleAt(HighlightedLine line, int offset) =>
        Innermost(line, offset, color => color.FontStyle);

    private static T? Innermost<T>(HighlightedLine line, int offset, Func<HighlightingColor, T?> property)
        where T : struct
    {
        T? result = null;
        foreach (var section in line.Sections)
        {
            if (section.Offset <= offset && offset < section.Offset + section.Length && section.Color is { } color
                && property(color) is { } value)
            {
                result = value;
            }
        }

        return result;
    }
}
