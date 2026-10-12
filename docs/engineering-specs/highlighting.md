YOUR AREA: theme-aware syntax highlighting for the NimPad editor (AvalonEdit 6.3.1.120). You own ONLY:
apps/windows/src/NimPad.App/Highlighting/** and apps/windows/tests/NimPad.App.Tests/Highlighting/**. (The editor
view that USES your API is built later by another engineer — design a small, documented API for them.)

READ FIRST: docs/design/tokens.md ("Syntax" tokens: s-key, s-str, s-num, s-lit, s-punc, s-com; plus Accent, Text),
apps/windows/src/NimPad.App/Themes/README.md (the WPF resource keys: SyntaxKeyColor/Brush, SyntaxStringColor, ...,
AccentColor, TextColor), apps/windows/src/NimPad.App/Themes/Tokens.Light.xaml / Tokens.Dark.xaml (exact values),
apps/windows/src/NimPad.Core/Domain (DocumentFormat enum + DocumentFormats helpers: the format list is plain,
markdown, json, xml, yaml, python, groovy, javascript, typescript, csharp, sql, powershell, ini, css, html), and the
deck page p-06 (JSON with highlighting, dark): docs/design/renders/p-06.png.
Design rule: "Syntax colors are used only when a note is recognized as code. Plain text stays one color."

BUILD:
1) Highlighting/SyntaxPalette: the six syntax token colors + accent + text for Light and Dark. Read them from the
   token ResourceDictionaries (load Themes/Tokens.Light.xaml / Tokens.Dark.xaml via pack URI, or accept a
   ResourceDictionary) so there is ONE source of truth — do not duplicate hex values in C#.
2) Highlighting/HighlightingProvider: `IHighlightingDefinition? Get(DocumentFormat format, bool dark)` returns null
   for plain, else a themed definition, cached per (format, theme) and thread-safe. Sources:
   - AvalonEdit built-ins, loaded by name from HighlightingManager.Instance ("MarkDown", "Json", "XML", "Python",
     "JavaScript", "C#", "PowerShell", "TSQL", "CSS", "HTML"; TypeScript may reuse JavaScript). Built-in
     definitions are shared singletons — NEVER mutate them; produce a themed COPY: either re-load the XSHD from
     the AvalonEdit assembly's embedded resource stream (ICSharpCode.AvalonEdit.Highlighting.Resources.<name>.xshd;
     list them via Assembly.GetManifestResourceNames) and deserialize with HighlightingLoader.LoadXshd +
     HighlightingLoader.Load into a fresh definition whose NamedHighlightingColors you then recolor, or an
     equivalent approach that guarantees isolation.
   - Your own embedded .xshd resources (Highlighting/Definitions/*.xshd, EmbeddedResource in the csproj — you may
     add the ItemGroup to NimPad.App.csproj; that is the only csproj edit allowed) for YAML (keys, strings,
     numbers, booleans/null, comments, anchors/aliases, document markers), Groovy (Java-like: keywords, strings
     incl. GStrings/triple quotes, numbers, comments, annotations) and INI/TOML (sections, keys, values,
     strings, numbers, booleans, comments).
   - Recolor EVERY named color of every definition to a design token through an explicit mapping table per
     source definition (e.g. Json: Bool->SyntaxLiteral, Number->SyntaxNumber, String->SyntaxString,
     FieldName->SyntaxKey, Null->SyntaxLiteral, Punctuation->SyntaxPunctuation; XML: Comment->SyntaxComment,
     XmlTag->SyntaxKey, AttributeName->SyntaxLiteral, AttributeValue->SyntaxString, Entity->SyntaxNumber, ...;
     MarkDown: Heading->SyntaxKey bold, Emphasis->italic only, StrongEmphasis->bold only, Code->SyntaxString,
     BlockQuote->SyntaxComment, Link->Accent, Image->Accent, LineBreak->SyntaxPunctuation;
     C#/JS/Python/PowerShell/TSQL/CSS/HTML: comments->SyntaxComment, strings/chars->SyntaxString,
     numbers->SyntaxNumber, keywords/types/tags/selectors->SyntaxKey, literals/constants/booleans/null->
     SyntaxLiteral, operators/punctuation->SyntaxPunctuation, preprocessor/attributes->SyntaxLiteral, anything
     else -> Text). Background colors from built-ins must be cleared (the editor surface is Surface; selection
     and current line are drawn by the editor). Keep font weight/style intent (bold keywords are NOT in the
     design: use normal weight for code keywords; Markdown headings bold).
   - A unit-testable guarantee: after recoloring, no named color of any themed definition carries a color that is
     not one of the palette's colors (or null) — i.e. no AvalonEdit default colors leak. Also unknown/unmapped
     color names must fall back to Text (and be listed by a diagnostic method so tests can assert the mapping
     tables are complete for every built-in definition).
3) Highlighting/LargeDocumentPolicy: pure static helpers deciding when highlighting must be OFF to keep typing
   instant: total length > 10 MB, or any line longer than 20,000 chars (scan efficiently over a string or
   ITextSource without allocating per line). Expose the thresholds as constants and a reason string for the
   status bar ("Plain Text (large file)").
4) Highlighting/README.md documenting the API for the editor engineer: how to get a definition for a tab's format
   and theme, when to re-query on theme change, the large-document policy.
5) Tests (apps/windows/tests/NimPad.App.Tests/Highlighting, STA where WPF types are needed, each test starting with
   Assert.SkipUnless(OperatingSystem.IsWindows(), "...")): every format x theme loads (plain -> null); themed
   copies are isolated (built-in definitions unchanged after themeing; light and dark copies differ); no leaked
   default colors; mapping completeness; your YAML/Groovy/INI definitions highlight representative samples (run a
   DocumentHighlighter over a TextDocument and assert spans for key/string/number/comment); large-document policy
   boundaries. Also make sure everything COMPILES on Linux and the solution builds with 0 warnings
   (`dotnet build apps/windows/NimPad.slnx`), `dotnet format` clean.

You are working in an ISOLATED GIT WORKTREE (your current working directory). Work only there; never touch
/home/user/nimpad directly (other engineers are editing it). When done and green, COMMIT on your worktree branch
(do not push, do not touch other branches) and report: worktree path, branch, commit SHA, files, API summary,
decisions, what is unverified (runtime behavior is only verifiable on Windows CI).
