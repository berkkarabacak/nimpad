using System.Diagnostics;
using FlaUI.Core.Definitions;
using NimPad.E2E.Infrastructure;
using Xunit;

namespace NimPad.E2E;

/// <summary>
/// The hierarchical workspace through the explorer sidebar: nested folders, a note moved by drag and drop, local search,
/// and the tree with its expansion state after a restart (spec §12 Phase 1, §14 Scenarios 1 and 3).
/// </summary>
public sealed class ExplorerTests
{
    /// <summary>
    /// Builds Projects › NimPad › Architecture from the explorer, drags the note from NimPad to Projects (it keeps its
    /// id), finds it with search, and gets the same tree, expansion and note back after closing and relaunching.
    /// </summary>
    [Fact]
    public void HierarchyCreateMoveSearch()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Ui.WindowsOnly);
        Scenario.Run(scenario =>
        {
            const string NoteText = "Layers: Core, Presentation, App";
            var dataDirectory = scenario.CreateDataDirectory();

            var app = scenario.Launch(dataDirectory);
            app.WaitForTabTitles(["Untitled 1"]);
            var explorer = new ExplorerPane(app);
            explorer.OpenWithShortcut();

            // Projects at the workspace root: the toolbar's New folder button starts an inline rename.
            explorer.ClickNewFolder();
            explorer.CompleteInlineRename("Projects");

            // NimPad inside Projects, and the note inside NimPad, from the folders' context menus.
            explorer.ChooseFromContextMenu("Projects", "New folder");
            explorer.CompleteInlineRename("NimPad");
            explorer.ChooseFromContextMenu("NimPad", "New note");
            explorer.CompleteInlineRename("Architecture");

            // The new note is open in the editor, under Projects › NimPad.
            app.WaitForTabTitles(titles => titles.Contains("Architecture"), "a tab titled \"Architecture\"");
            app.WaitForBreadcrumbs(["Projects", "NimPad", "Architecture"]);
            app.FocusEditor();
            app.TypeText(NoteText);
            var sinceEdit = Stopwatch.StartNew();
            app.WaitForEditorText(NoteText);
            app.WaitForAutosave("Architecture", sinceEdit);

            // Drag the note from NimPad onto Projects. A drag is a real mouse gesture; if the first one does not
            // register, try once more before failing.
            var noteRowId = Ui.IdOf(explorer.WaitForTreeRow("Architecture"))
                ?? throw new InvalidOperationException("The Architecture row has no AutomationId.");
            explorer.DragOnto("Architecture", "Projects");
            if (!Ui.TryWaitUntil(() => IsDirectlyInProjects(app.ReadBreadcrumbs()), Ui.Timeout))
            {
                explorer.DragOnto("Architecture", "Projects");
            }

            app.WaitForBreadcrumbs(["Projects", "Architecture"], absent: "NimPad");
            Assert.Equal(noteRowId, Ui.IdOf(explorer.WaitForTreeRow("Architecture")));

            // Local search finds the note, by the same id.
            explorer.Search("Architecture");
            var result = explorer.WaitForSearchResult("Architecture");
            Assert.Equal(AutomationIds.ExplorerSearchResultPrefix + NodeIdOf(noteRowId), Ui.IdOf(result));
            explorer.ClearSearch();
            explorer.WaitForTreeRow("Architecture");

            app.Close();

            // After a restart: the same tree, with Projects still expanded, and the note open where it was left.
            var restored = scenario.Launch(dataDirectory);
            var restoredExplorer = new ExplorerPane(restored);
            restoredExplorer.Open();
            restoredExplorer.WaitForTreeRow("Projects");
            Ui.WaitForValue(
                () => restoredExplorer.ExpandStateOf("Projects"),
                state => state == ExpandCollapseState.Expanded,
                "Projects to be restored expanded");
            restoredExplorer.WaitForTreeRow("NimPad");
            Assert.Equal(noteRowId, Ui.IdOf(restoredExplorer.WaitForTreeRow("Architecture")));

            // Collapsing Projects hides NimPad and the note's tree row (a Recent row of the note may stay); expanding
            // brings them back. That pins down the nesting without relying on indentation.
            var noteRows = restoredExplorer.FindRowsById(noteRowId).Count;
            restoredExplorer.SetExpanded("Projects", expanded: false);
            Ui.WaitUntil(
                () => restoredExplorer.FindRows("NimPad").Count == 0 && restoredExplorer.FindRowsById(noteRowId).Count == noteRows - 1,
                "collapsing Projects to hide NimPad and the Architecture tree row");
            restoredExplorer.SetExpanded("Projects", expanded: true);
            Ui.WaitUntil(
                () => restoredExplorer.FindRows("NimPad").Count == 1 && restoredExplorer.FindRowsById(noteRowId).Count == noteRows,
                "expanding Projects to show NimPad and the Architecture tree row again");

            restored.WaitForTabTitles(titles => titles.Contains("Architecture"), "the Architecture tab to be restored");
            restored.WaitForEditorText(NoteText);
            restored.WaitForBreadcrumbs(["Projects", "Architecture"], absent: "NimPad");
        });
    }

    private static bool IsDirectlyInProjects(IReadOnlyList<string> breadcrumbs)
    {
        var names = breadcrumbs.ToList();
        var projects = names.IndexOf("Projects");
        return projects >= 0 && names.LastIndexOf("Architecture") > projects && !names.Contains("NimPad");
    }

    private static string NodeIdOf(string rowAutomationId) =>
        rowAutomationId.StartsWith(AutomationIds.ExplorerRowPrefix, StringComparison.Ordinal)
            ? rowAutomationId[AutomationIds.ExplorerRowPrefix.Length..]
            : throw new InvalidOperationException($"Unexpected explorer row id {rowAutomationId}.");
}
