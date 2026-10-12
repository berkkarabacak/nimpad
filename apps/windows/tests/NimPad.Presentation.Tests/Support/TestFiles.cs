using System.Text;
using NimPad.Presentation.Platform;

namespace NimPad.Presentation.Tests.Support;

/// <summary>Files on disk for external-file tests (under the harness's temp directory).</summary>
internal static class TestFiles
{
    public static string Write(ShellHarness harness, string name, string text) => Write(harness, name, Encoding.UTF8.GetBytes(text));

    public static string Write(ShellHarness harness, string name, byte[] bytes)
    {
        var path = Path.Combine(harness.Directory.Combine("files"), name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}

/// <summary>A document whose snapshot fails (the App could not read its document).</summary>
internal sealed class FailingEditorBuffer(int length) : IEditorBuffer
{
    public long Version { get; private set; }

    public int TextLength => length;

    public string GetSnapshotText() => throw new InvalidOperationException("The document could not be read.");

    public void Touch() => Version++;
}
