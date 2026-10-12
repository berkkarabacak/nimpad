# Syntax highlighting

Theme-aware AvalonEdit highlighting for the editor. Design rule: syntax colors are used only when a note is
recognized as code; plain text stays one color. Everything is in `NimPad.App.Highlighting`.

| File | What |
|---|---|
| `HighlightingProvider.cs` | `Get(format, dark)`: the themed definition for a tab, cached per (format, theme). |
| `SyntaxPalette.cs` | The 8 colors a definition may use, read from the token dictionaries. |
| `LargeDocumentPolicy.cs` | When highlighting must be off (huge text, very long lines). |
| `HighlightingSources.cs` | Internal: which XSHD each format uses and the color → token mapping tables. |
| `Definitions/*.xshd` | NimPad's own definitions: YAML, Groovy, INI/TOML (embedded resources). |

## Using it in the editor

```csharp
using NimPad.App.Highlighting;

// Whenever the editor shows a document: tab activation, format change, theme change, (re)load.
void ApplyHighlighting(TextEditor editor, DocumentFormat format, bool dark)
{
    var reason = LargeDocumentPolicy.Check(editor.Document);          // on the UI thread
    editor.SyntaxHighlighting = reason == LargeDocumentReason.None
        ? HighlightingProvider.Default.Get(format, dark)              // null for DocumentFormat.Plain
        : null;
    // Status bar mode: the format's name, or LargeDocumentPolicy.StatusLabel ("Plain Text (large file)").
}
```

- **`HighlightingProvider.Default.Get(format, dark)`** returns `null` for `Plain` (the editor's `TextBrush` is the only
  color) and otherwise a themed `IHighlightingDefinition`. `dark` is `ThemeService.IsDark`.
- **Same arguments, same instance.** Definitions are cached forever (one per definition and theme, about 30 in total),
  so calling `Get` on every tab switch is cheap. TypeScript uses the JavaScript definition. Thread-safe; the first
  call per definition parses its XSHD (a few milliseconds).
- **Re-query on theme change.** A definition carries fixed colors for one theme, so it cannot follow
  `DynamicResource`. Handle `ThemeService.ThemeChanged` by calling `Get(format, themes.IsDark)` again for every open
  editor and assigning the result to `TextEditor.SyntaxHighlighting`.
- **Re-query on format change** (the user picks another mode, or a rename changes the extension).
- **Do not modify the returned definitions.** They are shared by every editor; their colors are frozen (assigning
  one throws).
- **Backgrounds belong to the editor.** Definitions never set a background, underline, font family or size. The
  editor paints the surface (`SurfaceBrush`), selection (`SelectionBrush`), current line (`CurrentLineBrush`) and find
  matches from the tokens; its `Foreground` should be `TextBrush`.

`new HighlightingProvider(lightPalette, darkPalette)` (palettes from `SyntaxPalette.FromResources(dictionary)`)
builds a separate cache, for tests or a preview with other tokens.

## Large documents

AvalonEdit highlights each visible line with regular expressions; huge texts and very long lines (minified JSON,
logs) make typing lag. Highlighting is off when:

| Constant | Value | Check |
|---|---|---|
| `LargeDocumentPolicy.MaxTextLength` | 10 MB (10,485,760 UTF-16 characters) | `IsTextTooLong(document.TextLength)` |
| `LargeDocumentPolicy.MaxLineLength` | 20,000 characters (without the line break) | `IsLineTooLong(line.Length)` |

- `Check(string | ReadOnlySpan<char> | ITextSource)` returns `None`, `TextTooLong` or `LineTooLong`. It never
  allocates per line; a text of at most 20,000 characters is not scanned at all, and a `TextDocument` is checked
  through its line lengths without reading the text (about 10 ms for 10 MB). Call it on the document's owner
  thread, or pass `document.CreateSnapshot()` from another thread (for example after an off-thread load).
- `ShouldDisableHighlighting(...)` is the `bool` shortcut.
- While the policy holds, the status bar shows `LargeDocumentPolicy.StatusLabel` ("Plain Text (large file)") instead
  of the format. The same check can drive word wrap (the implementation plan turns both off).
- **Check on:** load and tab activation (full check). **While typing**, keep it cheap: in `Document.Changed`, test
  `IsTextTooLong(document.TextLength)` and `IsLineTooLong(line.Length)` for the lines the change touched; turn
  highlighting off when one is exceeded. Turning it back on can wait for the next full check (tab activation) so
  the editor does not flicker between modes.

## How definitions are themed

1. The XSHD is parsed fresh: AvalonEdit built-ins from the AvalonEdit assembly's embedded resources
   (`ICSharpCode.AvalonEdit.Highlighting.Resources.<file>.xshd`), NimPad's from `Definitions/`. The shared
   instances in `HighlightingManager.Instance` are never touched (a test checks their colors and rules afterwards).
2. Imports (Markdown → C# code blocks, C# → XmlDoc, HTML → JavaScript) resolve to the themed copies of the same theme.
3. Every named color is mapped to a token through the explicit table in `HighlightingSources.cs`; background,
   underline, font family and size are cleared, and code is never bold (only Markdown headings, Markdown strong
   emphasis and INI section headers are bold; Markdown emphasis is italic). A name missing from the table falls
   back to `text` and is listed by `HighlightingProvider.GetUnmappedColorNames()` (a test asserts it is empty).
4. Inline colors without a name (C#'s bold red `TODO`) are cleared, so they keep the surrounding color.
5. Everything is frozen. Result: only the 8 palette colors can appear (a test walks every rule and span).

| Token | Used for |
|---|---|
| `s-key` | Keys and properties (JSON field names, YAML/INI keys, CSS properties), keywords, types, tags, selectors, Markdown and INI headings |
| `s-str` | Strings and characters, YAML/INI unquoted values, Markdown code, XML CDATA |
| `s-num` | Numbers, TOML dates, XML/HTML entities |
| `s-lit` | `true`/`false`/`null`, constants, preprocessor directives, XML/HTML attributes, annotations, YAML anchors/tags |
| `s-punc` | Punctuation and operators, YAML document markers |
| `s-com` | Comments (also XML doc comments and Markdown block quotes) |
| `accent` | Markdown links and images |
| `text` | Everything else: identifiers, method calls, string interpolation, CSS values, PowerShell variables and commands |

Formats: Markdown (`MarkDown`), JSON (`Json`), XML (`XML`), Python (`Python`), JavaScript and TypeScript
(`JavaScript`), C# (`C#`), SQL (`TSQL`), PowerShell (`PowerShell`), CSS (`CSS`), HTML (`HTML`) are AvalonEdit
built-ins; YAML, Groovy (also `.gradle`, Jenkinsfile) and INI (`.ini`, `.cfg`, `.conf`, `.toml`, `.editorconfig`)
are NimPad's.

## Changing or adding a definition

- Edit the table in `HighlightingSources.cs`; `MappingTablesNameEveryColorOfEveryDefinition` fails on unmapped or
  stale names. New XSHD files go in `Definitions/` (picked up by the `EmbeddedResource` glob in `NimPad.App.csproj`)
  with names only on `<Color>` (no color values: the tokens are the single source of truth).
- AvalonEdit semantics worth knowing: a span's begin regex sees the whole line, but a rule only sees the text between
  spans (its look-behind and `^`/`$` stop there); at the same position the element listed first wins; rule bodies
  and `<Begin>`/`<End>` use `IgnorePatternWhitespace` (write spaces and `#` inside `[...]`); a rule must never match
  an empty string.
- Keep every regex linear: it is tried at every position of lines up to 20,000 characters. Put a look-behind after
  the token's first character (or behind a look-ahead on it), make whitespace runs atomic (`(?>[ \t]*)`), and never
  follow a lazy loop with a look-ahead that rescans whitespace. A first version of the YAML rules took 12 seconds
  on one line of spaces; the rewritten ones take under 60 ms on any 20,000-character line.
- Add a sample to `NimPad.App.Tests/Highlighting/SampleHighlightingTests.cs`.

## Known limits

- TypeScript-only syntax (types, `interface`, generics) is highlighted as JavaScript.
- AvalonEdit's Python definition lists `True`/`False`/`None` among keywords (`s-key`). Its number rule is broken
  upstream (it never matches plain numbers); NimPad's copy replaces it (`FixPythonNumbers`).
- YAML: the contents of block scalars (`|`, `>`) are not recognized as text, so a `key: value` line inside one is
  colored as a mapping.
- Groovy: `/slashy/` strings are not recognized (they look like division), and a `}` inside `${...}` ends it.
- INI: `;` and `#` start a comment at the line start or after whitespace, so `color = #fff` is a comment (as in TOML).
