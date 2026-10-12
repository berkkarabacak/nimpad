using NimPad.Core.Domain;
using NimPad.Core.Ids;
using Xunit;

namespace NimPad.Core.Tests.Domain;

public class FormatAndSettingsTests
{
    [Theory]
    [InlineData("config.json", "x", DocumentFormat.Json)]
    [InlineData("notes.txt", "{\"a\": 1}", DocumentFormat.Plain)]
    [InlineData("server.log", "<?xml version=\"1.0\"?><a/>", DocumentFormat.Plain)]
    [InlineData("data", "{\"a\": [1, 2]}", DocumentFormat.Json)]
    [InlineData("data", "﻿  [1, 2,]", DocumentFormat.Json)]
    [InlineData("data", "{ not json", DocumentFormat.Plain)]
    [InlineData("feed", "<?xml version=\"1.0\"?>\n<rss/>", DocumentFormat.Xml)]
    [InlineData("page", "<!doctype html>\n<html></html>", DocumentFormat.Html)]
    [InlineData("deploy", "#!/usr/bin/env python3\nprint(1)", DocumentFormat.Python)]
    [InlineData("build", "#!/usr/bin/env pwsh\nWrite-Host hi", DocumentFormat.PowerShell)]
    [InlineData("tool", "#!/usr/bin/env node\nconsole.log(1)", DocumentFormat.JavaScript)]
    [InlineData("script", "#!/bin/sh\necho hi", DocumentFormat.Plain)]
    [InlineData("Meeting 2026.10", "{\"a\": 1}", DocumentFormat.Json)]
    [InlineData("Jenkinsfile", "pipeline {}", DocumentFormat.Groovy)]
    [InlineData(null, "plain words", DocumentFormat.Plain)]
    public void DetectsTheFormatFromTheNameThenTheContent(string? name, string text, DocumentFormat expected)
    {
        Assert.Equal(expected, DocumentFormats.Detect(name, text));
    }

    [Fact]
    public void DefaultSettingsMatchTheDesign()
    {
        var settings = AppSettings.Default;

        Assert.Equal(ThemePreference.System, settings.Theme);
        Assert.Equal("Cascadia Mono", settings.EditorFontFamily);
        Assert.Equal(14, settings.EditorFontSize);
        Assert.False(settings.WordWrap);
        Assert.True(settings.ShowLineNumbers);
        Assert.Equal(4, settings.TabSize);
        Assert.True(settings.RestoreTabs);
        Assert.False(settings.StartWithWindows);
        Assert.Equal("Ctrl+Alt+N", settings.GlobalHotkey);
        Assert.Equal(100, settings.Zoom);
        Assert.Empty(settings.Validate());
    }

    [Fact]
    public void InvalidSettingsAreReported()
    {
        var settings = AppSettings.Default with { TabSize = 3, EditorFontSize = 200, Zoom = 5, EditorFontFamily = " ", Theme = (ThemePreference)9 };
        Assert.Equal(5, settings.Validate().Count);
    }

    [Fact]
    public void UiStateComparesExpandedFoldersByValue()
    {
        var a = NodeId.New();
        var b = NodeId.New();
        var first = UiState.Default with { ExpandedFolderIds = [a, b], SelectedNodeId = a };
        var second = UiState.Default with { ExpandedFolderIds = new List<NodeId> { a, b }, SelectedNodeId = a };

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, second with { ExpandedFolderIds = [b, a] });
        Assert.Equal(288, UiState.Default.SidebarWidth);
        Assert.Equal(ExplorerSortOrder.Manual, UiState.Default.SortOrder);
        Assert.False(UiState.Default.IsContextPanelVisible);
    }
}
