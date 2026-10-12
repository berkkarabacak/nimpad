using System.IO;
using System.Windows;
using Microsoft.Win32;
using NimPad.Presentation.Platform;

namespace NimPad.App.Platform;

/// <summary>The Windows Open and Save dialogs, owned by the main window.</summary>
public sealed class FileDialogService : IFileDialogService
{
    /// <summary>The filter offered by both dialogs.</summary>
    public const string Filter = "Text files|*.txt;*.md;*.markdown;*.log;*.json;*.xml;*.yaml;*.yml;*.ini;*.toml;*.cfg;*.conf;*.csv|All files|*.*";

    private readonly Func<Window?> _owner;

    /// <summary>Creates the service; <paramref name="owner"/> returns the window the dialogs belong to.</summary>
    public FileDialogService(Func<Window?>? owner = null)
    {
        _owner = owner ?? (() => Application.Current?.MainWindow);
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> PickFilesToOpen()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open",
            Filter = Filter,
            FilterIndex = 2,
            Multiselect = true,
            CheckFileExists = true,
        };
        return dialog.ShowDialog(Owner()) == true ? dialog.FileNames : [];
    }

    /// <inheritdoc/>
    public string? PickSaveFile(string suggestedName, string? initialDirectory)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save as",
            Filter = Filter,
            FilterIndex = 2,
            FileName = suggestedName ?? string.Empty,
            AddExtension = false,
            OverwritePrompt = true,
        };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog(Owner()) == true ? dialog.FileName : null;
    }

    private Window? Owner()
    {
        var owner = _owner();
        return owner is { IsLoaded: true } ? owner : null;
    }
}
