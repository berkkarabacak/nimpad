using NimPad.Core.Domain;
using NimPad.Presentation.Integration;
using Xunit;

namespace NimPad.Presentation.Tests.Integration;

public sealed class FileDialogFiltersTests
{
    [Fact]
    public void TheOpenDialogListsAllFilesFirstThenTextMarkdownAndJson()
    {
        var parts = FileDialogFilters.Open.Split('|');

        Assert.Equal(FileDialogFilters.OpenFilters.Count * 2, parts.Length);
        Assert.Equal("All files (*.*)", parts[0]);
        Assert.Equal("*.*", parts[1]);
        Assert.StartsWith("Text files (*.txt;", parts[2], StringComparison.Ordinal);
        Assert.Equal("Markdown (*.md;*.markdown)", parts[4]);
        Assert.Equal("*.md;*.markdown", parts[5]);
        Assert.Equal("JSON (*.json;*.jsonc)", parts[6]);
        Assert.Equal(1, FileDialogFilters.DefaultOpenFilterIndex);
    }

    [Fact]
    public void EveryListedExtensionOpensInTheModeItIsListedUnder()
    {
        foreach (var filter in FileDialogFilters.OpenFilters.Where(filter => filter.Format is not null))
        {
            Assert.NotEmpty(filter.Extensions);
            foreach (var extension in filter.Extensions)
            {
                Assert.StartsWith(".", extension, StringComparison.Ordinal);
                Assert.Equal(filter.Format, DocumentFormats.FromName("file" + extension));
            }
        }
    }

    [Fact]
    public void EverySyntaxModeHasAnEntryAndNoExtensionIsListedTwice()
    {
        var formats = FileDialogFilters.OpenFilters.Select(filter => filter.Format).OfType<DocumentFormat>().ToHashSet();
        Assert.Equal(DocumentFormats.All.ToHashSet(), formats);

        var extensions = FileDialogFilters.OpenFilters.SelectMany(filter => filter.Extensions).ToList();
        Assert.Equal(extensions.Count, extensions.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(FileDialogFilters.OpenFilters, filter => filter.DisplayText.Contains('|', StringComparison.Ordinal));
    }
}
