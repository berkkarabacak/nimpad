YOUR AREA: the Explorer sidebar view of the NimPad WPF app. You own ONLY:
apps/windows/src/NimPad.App/Views/Explorer/** (replace the stub ExplorerView.xaml(.cs) that the shell engineer
left; keep its class name NimPad.App.Views.Explorer.ExplorerView and its public surface) and
apps/windows/tests/NimPad.App.Tests/Explorer/**. If the ExplorerViewModel genuinely lacks something the view
needs, you may make a minimal, tested addition in apps/windows/src/NimPad.Presentation/Explorer/** — keep it small.

Read first: Presentation README + ExplorerViewModel/ExplorerRowViewModel, Themes/README.md (use the design
system's tokens, text styles, icons, SearchBox, NoteRowItem, menus — never hard-code colors), docs/design/README.md
("Adaptations": tree-first sidebar), deck renders in
docs/design/renders/ (p-07 sidebar open,
p-08 sidebar states: search results with highlighted matches, sort menu, Trash with Restore/Delete forever on
hover, empty Trash, no notes yet, no results; p-25/p-26 rows & menus; p-27 keyboard: "Notes sidebar").

BUILD (288 px wide by default; the shell hosts and sizes you):
- Top: SearchBox (32 px, "Search all notes", Ctrl+B focuses it; Down moves into the list; Esc clears, a second Esc
  returns focus to the editor via the view model's event). Then a small toolbar row: item count text (Caption,
  Text3, e.g. "24 notes"), sort menu button ("⇅ Manual" / "Last edited" / "Name, A to Z" with a check on the
  active one — p-08 sort menu look), New note (+) and New folder icon buttons with tooltips.
- ONE virtualized ListBox (VirtualizingStackPanel, VirtualizationMode=Recycling, ScrollUnit=Pixel,
  IsDeferredScrollingEnabled=False) bound to ExplorerViewModel.Rows, with a DataTemplateSelector for row kinds:
  section header (Micro caps text, Text3, e.g. "PINNED", "RECENT", "MY WORKSPACE"; not selectable/focusable),
  note row for Pinned/Recent/Search results (title 13.5/600 + time right-aligned in 11.5 Text3, preview line
  12.5 Text2 trimmed; unsaved/pending accent dot like p-07; search results highlight each match range with the
  Match brush background — build Runs from the highlight ranges), tree row (indent = Depth * 16 px + 8; chevron
  (ChevronRight rotated/ChevronDown) only for folders with children, click toggles; Folder/FolderOpen or
  Document icon; name in Control style; pinned rows show a small pin glyph; inline rename TextBox when
  IsRenaming — text preselected, Enter commits, Esc cancels, lost focus commits), trash row (title + "Deleted
  today"/"12 days left", preview; Restore and Delete forever buttons appear on hover/focus like p-08), empty
  states (No notes yet + "New note Ctrl+N" primary button; No notes match + Clear search link; Trash is empty).
  Selected row uses AccentSoft (NoteRowItem style), inset focus ring, 32 px tree rows / 52 px note rows (keep
  heights fixed so virtualization is smooth). Trash entry at the bottom (pinned footer row outside the list:
  Trash icon + "Trash" + count, like p-07), switching to the Trash mode with a back button "‹ Trash" header as in
  p-08.
- Keyboard (p-27): Up/Down/Home/End move; Right expands / moves to first child; Left collapses / moves to parent;
  Enter opens the note (or toggles a folder); F2 renames; Delete moves to Trash (Undo toast comes from the VM);
  Ctrl+C / context-menu Copy link; in Trash: Enter restores, Delete deletes forever after confirmation; type-ahead
  is not required. Focus stays in the list after operations; selection follows keyboard focus.
- Context menus (design menu style; gesture text right-aligned): folder: New note, New folder, Rename F2, Pin/Unpin,
  Duplicate, Copy link, Move to Trash Del; document: Open, Rename F2, Pin/Unpin, Duplicate Ctrl+Shift+D,
  Copy link, Export…, Move to Trash Del; workspace header/empty area: New note, New folder.
- Drag & drop (custom, smooth): start after SystemParameters.MinimumHorizontal/VerticalDragDistance; private
  DataFormat "NimPad.NodeIds" (multi-select not required, single node is fine); while dragging, compute
  DropPosition from the pointer's Y within the target row (top 25% Before, middle Inside for folders, bottom 25%
  After; documents only Before/After) and draw an adorner: an accent insertion line (2 px, indented to the
  target depth) or an AccentSoft highlight + accent outline for Inside; ask ExplorerViewModel.CanDrop and show
  the not-allowed cursor/no adorner when false; auto-scroll when the pointer is within 24 px of the list edges;
  hover over a collapsed folder for 600 ms expands it; Esc cancels; drop calls DropAsync. Files dropped from
  Windows Explorer are NOT handled here — let them bubble to the window (don't mark them handled).
- Reveal/scroll requests from the VM: scroll the row into view and focus it.
- Accessibility: a custom AutomationPeer for tree rows exposing ExpandCollapse pattern, Level (Depth+1),
  PositionInSet/SizeOfSet where cheap, Name = node name; AutomationIds: ExplorerSearch, ExplorerList,
  ExplorerRow_<nodeId>, ExplorerNewNote, ExplorerNewFolder, ExplorerSort, ExplorerTrash, TrashBack,
  TrashEmptyButton, RestoreButton, DeleteForeverButton.
- Performance: no per-row heavy visuals (no DropShadowEffect on rows, no bitmap effects); templates must be
  light; 10,000 rows must scroll smoothly.
Tests (App.Tests/Explorer, STA, Windows-only skip): build the view over a real ExplorerViewModel on a temp data
dir with a nested tree; measure/arrange; assert virtualization is on, templates resolve for every row kind in
both themes, drop-position computation (factor that math into a pure static class and unit test it — that class
can live in Views/Explorer and be tested on Windows; ALSO put a copy of the pure math tests in the Presentation
tests if you placed the math there). `dotnet build apps/windows/NimPad.slnx` must stay green on Linux;
Core + Presentation tests green.

UI AUTOMATION CONTRACT (mandatory): expose exactly the AutomationIds, Names and ItemStatus values listed for your
area in docs/engineering-specs/automation-ids.md — the
FlaUI end-to-end tests are written against it. Where this spec names a different id, the contract wins.

END-TO-END EXPECTATIONS (the FlaUI tests on Windows CI check these):
- Folder context menu items are named exactly "New note" and "New folder" (AutomationProperties.Name = the
  visible header); invoking them starts an inline rename in ExplorerRenameBox, which must keep keyboard focus after
  the context menu closes (focus it at DispatcherPriority.Input/ContextIdle after the menu closes); typing a name
  + Enter commits.
- The workspace section ("MY WORKSPACE") is the LAST section of the list (after PINNED and RECENT).
- Tree rows keep the same AutomationId (ExplorerRow_<nodeId>) across moves and relaunches; folder rows expose the
  ExpandCollapse pattern; expansion state is restored on relaunch.
- Drag & drop works with a real mouse drag performed as: left button down on the source row, several small moves
  past the system drag threshold, a short hover over the target row, button up (Inside position for a folder:
  middle of the row).
- With an empty workspace the search box may be disabled; once any note exists, typing in ExplorerSearch shows
  ExplorerSearchResult_<nodeId> rows named after the notes; Esc clears the search.
