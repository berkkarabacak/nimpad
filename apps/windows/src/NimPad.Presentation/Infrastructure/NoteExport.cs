using System.Diagnostics;
using NimPad.Core.Domain;
using NimPad.Core.Ids;
using NimPad.Presentation.Editor;
using NimPad.Presentation.Feedback;

namespace NimPad.Presentation.Infrastructure;

/// <summary>Ctrl+S on a managed note, and the explorer's Export: writes the note to a file the user picks.</summary>
internal static class NoteExport
{
    /// <summary>Flushes the note's autosave, asks for a file name and exports the stored text (UTF-8, LF).</summary>
    /// <returns><see langword="true"/> when a file was written.</returns>
    public static async Task<bool> ExportAsync(ViewModelContext context, NodeId nodeId)
    {
        var node = context.Model.Get(nodeId) ?? await context.Core.Nodes.GetNodeAsync(nodeId);
        if (node is null || !node.IsDocument)
        {
            return false;
        }

        try
        {
            await context.Core.Autosave.FlushAsync(nodeId);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Export of {nodeId}: the final save failed: {ex.Message}");
            context.Toasts.ShowError($"{DisplayText.Quote(node.Name)} couldn't be saved, so it wasn't exported.");
            return false;
        }

        var suggested = FormatInfo.SuggestFileName(node.Name, node.Format ?? DocumentFormat.Plain);
        var path = context.Platform.FileDialogs.PickSaveFile(suggested, context.LastExportDirectory);
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            await context.Core.ExternalFiles.ExportAsync(nodeId, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Toasts.ShowError($"Couldn't export {DisplayText.Quote(node.Name)}. {ex.Message}");
            return false;
        }

        context.LastExportDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
        context.Toasts.Show($"Exported {DisplayText.Quote(node.Name)} to {DisplayText.Quote(Path.GetFileName(path))}", ToastKind.Success);
        return true;
    }
}
