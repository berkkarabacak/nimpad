YOUR AREA: the editor of the NimPad WPF app. You own ONLY: apps/windows/src/NimPad.App/Views/Editor/**
(replace the stub EditorView + EditorHost the shell engineer left; keep class names
NimPad.App.Views.Editor.EditorView and its public surface), and apps/windows/tests/NimPad.App.Tests/Editor/**. If the Presentation
view models (EditorViewModel, FindReplaceViewModel/TextSearchEngine, TabViewModel, StatusBarViewModel) genuinely
lack something, make a minimal, tested addition in apps/windows/src/NimPad.Presentation/** (only in those
classes) — keep it small.

Read first: Presentation README + those view models, Themes/README.md (tokens: Surface, Text, Selection,
CurrentLine, Match, MatchCurrent, Syntax*; FlyoutCard; IconToggleButton; TextBox; SmallButton), the stub
EditorHost the shell engineer wrote, docs/design/README.md and renders in
docs/design/renders/ (p-04/p-05 editor,
p-06 JSON with line numbers, p-09 find & replace panel, p-27 keyboard "Editor" + "Find and replace", p-29 empty
note hint / large note opening / find no matches).

BUILD:
1) EditorView/EditorHost: exactly ONE AvalonEdit TextEditor for all tabs. Keep a TextDocument per tab
   (dictionary keyed by TabId; created lazily on first activation from TabViewModel.LoadAsync; disposed when the
   tab closes). On tab switch: store the outgoing tab's caret/selection/scroll into the TabViewModel, set
   editor.Document, restore the incoming tab's caret/selection/scroll at DispatcherPriority.Loaded (after
   layout), keep undo stacks per document. Large files: show the loading state (p-29: muted, "Opening “x” ·
   12.4 MB" progress with Cancel for > 5 MB); build the TextDocument off the UI thread when > 1 MB and hand it
   over with SetOwnerThread before assigning. Wrap each TextDocument as IEditorBuffer (Version from
   document.Version / a counter incremented on Changed; GetSnapshotText via CreateSnapshot().Text taken on the UI
   thread through the dispatcher when called off-thread) and call TabViewModel.AttachBuffer/OnBufferChanged on
   every change (cheap). Never call Document.Text in hot paths; use TextLength for counts.
2) Editor options & visuals: padding 20 top / 32 sides (24 in Medium/Narrow — bind ShellViewModel's value) via
   TextArea.Margin or a padding margin; font from settings (EditorFontFamily fallback "Cascadia Mono, Consolas"),
   size = FontSize * Zoom; LineHeight close to 22/14 ratio if feasible (AvalonEdit uses font line spacing — don't
   fight it with hacks); word wrap, line numbers (LineNumbersForeground Text3, separator removed/quiet), tab size
   (IndentationSize, ConvertTabsToSpaces=false by default — Notepad behavior), Background Surface, Foreground
   Text, SelectionBrush Selection, SelectionForeground null, SelectionBorder null, CurrentLineBackground
   CurrentLine (HighlightCurrentLine true), caret brush Accent, all updated live on theme change (subscribe to
   ThemeService.ThemeChanged / DynamicResource). Disable AvalonEdit's built-in hyperlink & email handling
   (Options.EnableHyperlinks = false, EnableEmailHyperlinks = false). Ctrl+wheel zooms via the view model;
   Ctrl+Plus/Minus/0 are shell commands. Breadcrumbs: one quiet Caption/Text3 line inside the top padding for
   managed notes (segments clickable -> reveal in explorer; "›" separators; external files show the full path).
   Empty-note hint (p-29): "Start typing. It saves on its own." in Text3 drawn as an overlay at the first line
   position when ShowEmptyHint is true; it disappears on the first keystroke and is never part of the document.
   Context menu: Undo, Redo, Cut, Copy, Paste, Delete, Select all (design menu style).
   Shortcuts: Ctrl+Z, Ctrl+Shift+Z and Ctrl+Y redo, Tab/Shift+Tab indent/outdent selection (AvalonEdit default),
   standard navigation. IMPORTANT: Tab types a tab; Ctrl+Tab and F6 must escape to the shell (coordinate: the
   shell handles them in PreviewKeyDown; don't swallow them).
3) Highlighting: ALREADY BUILT by another engineer in apps/windows/src/NimPad.App/Highlighting/** (read its
   README.md). Do not modify it beyond tiny, tested fixes. Integrate it: on tab activation and on format change
   set editor.SyntaxHighlighting = HighlightingProvider.Get(format, isDark); on ThemeService.ThemeChanged
   re-query for the active document; apply LargeDocumentPolicy (highlighting off, and the status bar mode text
   shows the policy's reason) for very large documents or very long lines.
4) Find & replace panel (p-09; FlyoutCard, top-right of the editor, 8 px from edges; spans the full width in
   Narrow layout): Find TextBox with placeholder, toggles Aa (MatchCase, Alt+C), ab (WholeWord, Alt+W),
   .* (Regex, Alt+R) as IconToggleButtons, count text "1 of 2" / "No results" (Text3) / regex error (Err text
   under the field, e.g. 'Unmatched “(”'), previous/next buttons (Shift+Enter/Enter), close X (Esc → focus returns
   to the editor with the current match selected); Replace row when IsReplaceMode (Ctrl+H): Replace TextBox,
   "Replace" (Enter in the replace box) and "Replace all" (Ctrl+Enter) SmallButtons. Tab order: Find, three
   options, previous, next, Replace, Replace, Replace all, Close (p-27). Highlight all matches with the Match
   brush and the current one with MatchCurrent via an IBackgroundRenderer (KnownLayer.Selection) that only
   renders matches within the visible lines (fast for many matches); selecting the current match scrolls it into
   view (centered when off-screen). Replace all applies all edits in ONE document update / undo group
   (document.BeginUpdate/EndUpdate, applying edits from the end backwards). Ctrl+F pre-fills from a single-line
   selection and selects the field text.
5) Links (nimpad://note/<uuid> and http/https): a VisualLineElementGenerator that renders links underlined in
   Accent; Ctrl+click (and Ctrl+Enter on a link at the caret) opens: nimpad links -> ShellViewModel opens the note
   by id (works after moves — ids, not paths); web links -> IShellLauncher (only http/https; never launch other
   schemes). Hand cursor only while Ctrl is held. A "Copy link" for the current note lives in the explorer/tab
   menus (not yours); but add editor context-menu "Insert link to note…" ONLY if trivial — otherwise skip.
6) Status bar feed: on caret/selection change call StatusBarViewModel.UpdateCaret(line, col, selectionLength,
   textLength) — throttle to at most once per render frame.
Tests (App.Tests/Editor, STA, Windows-only skip): highlighting definitions load for every format in both themes
and every named color maps to a token (no default AvalonEdit colors leak); the themed-definition cache; the match
renderer only enumerates visible matches; tab switching preserves per-tab undo stacks and caret; replace-all is a
single undo step; link generator finds nimpad/http links and ignores other schemes (pure parsing logic should be
in a class testable without WPF — put it in Presentation if natural, with tests there that run on Linux).
`dotnet build apps/windows/NimPad.slnx` green on Linux; Core + Presentation tests green.

UI AUTOMATION CONTRACT (mandatory): expose exactly the AutomationIds, Names and ItemStatus values listed for your
area in docs/engineering-specs/automation-ids.md — the
FlaUI end-to-end tests are written against it. Where this spec names a different id, the contract wins.

END-TO-END EXPECTATIONS (the FlaUI tests on Windows CI check these):
- The TextEditor has AutomationId "Editor" and keeps AvalonEdit's automation peer, so UIA ValuePattern returns
  the document text; UIA SetFocus on it (or a click) puts keyboard focus in the TextArea.
- Breadcrumbs (AutomationId "Breadcrumbs", segment Names = folder/note names) update live when the note is moved.
- The caret position is restored exactly after a relaunch (e.g. "Ln 2, Col 7" before close and after relaunch).
- External files keep their exact text including CRLF line endings in the editor; Ctrl+S saves them in place
  with the original encoding (UTF-16 LE BOM stays UTF-16 LE BOM).
