using System.Windows.Controls;

namespace NimPad.App.Shell;

/// <summary>"No notes open" with New note and Reopen closed tab, shown when every tab is closed (design page 29).</summary>
public partial class EmptyStateView : UserControl
{
    /// <summary>Creates the view.</summary>
    public EmptyStateView()
    {
        InitializeComponent();
    }

    /// <summary>Moves keyboard focus to New note.</summary>
    /// <returns><see langword="true"/> when it took focus.</returns>
    public bool FocusNewNote() => NewNoteButton.Focus();
}
