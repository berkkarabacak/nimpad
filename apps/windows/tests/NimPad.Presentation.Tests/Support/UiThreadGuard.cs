using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.ComponentModel;
using NimPad.Presentation.Shell;
using NimPad.Presentation.Tabs;

namespace NimPad.Presentation.Tests.Support;

/// <summary>
/// Records every PropertyChanged / CollectionChanged a shell's view models raise off the UI thread. In the App such a
/// change reaches WPF bindings from a timer or Core thread and throws, so a test that sees one fails.
/// </summary>
internal sealed class UiThreadGuard(TestUiThread ui)
{
    private readonly ConcurrentQueue<string> _violations = new();

    /// <summary>What was raised off the UI thread, with the stack that raised it.</summary>
    public IReadOnlyCollection<string> Violations => _violations;

    /// <summary>Watches the shell, its children, its collections and every tab (also tabs opened later).</summary>
    public void Watch(ShellViewModel shell)
    {
        WatchProperties(shell, "Shell");
        WatchProperties(shell.Tabs, "Tabs");
        WatchProperties(shell.Editor, "Editor");
        WatchProperties(shell.StatusBar, "StatusBar");
        WatchProperties(shell.FindReplace, "FindReplace");
        WatchProperties(shell.Explorer, "Explorer");
        WatchProperties(shell.Settings, "Settings");
        WatchProperties(shell.Shortcuts, "Shortcuts");
        WatchCollection(shell.Explorer.Rows, "Explorer.Rows");
        WatchCollection(shell.Toasts.Toasts, "Toasts");
        WatchCollection(shell.Banners.Banners, "Banners");
        WatchCollection(shell.Tabs.Tabs, "Tabs.Tabs");
        foreach (var tab in shell.Tabs.Tabs)
        {
            WatchProperties(tab, "Tab");
        }

        shell.Tabs.Tabs.CollectionChanged += (_, e) =>
        {
            if (e.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Replace))
            {
                return;
            }

            foreach (var tab in e.NewItems?.OfType<TabViewModel>() ?? [])
            {
                WatchProperties(tab, "Tab");
            }
        };
    }

    private void WatchProperties(INotifyPropertyChanged source, string name) =>
        source.PropertyChanged += (_, e) => Check($"{name}.{e.PropertyName}");

    private void WatchCollection(INotifyCollectionChanged source, string name) =>
        source.CollectionChanged += (_, e) => Check($"{name} {e.Action}");

    private void Check(string what)
    {
        if (!ui.CheckAccess())
        {
            _violations.Enqueue($"{what} was raised on thread {Environment.CurrentManagedThreadId}:{Environment.NewLine}{Environment.StackTrace}");
        }
    }
}
