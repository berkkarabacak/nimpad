using Xunit;

namespace NimPad.App.Tests.Themes;

/// <summary>Guards for the design-system XAML sources (no WPF needed; skipped when the sources are not found).</summary>
public class XamlSourceTests
{
    [Fact]
    public void EveryResourceReferenceResolves()
    {
        var audit = XamlResourceAudit.TryCreate();
        Assert.SkipWhen(audit is null, "Design-system sources not found next to the test binaries.");
        Assert.Empty(audit!.FindReferenceProblems());
    }

    [Fact]
    public void ReadmeDocumentsEveryResourceKey()
    {
        var audit = XamlResourceAudit.TryCreate();
        Assert.SkipWhen(audit is null, "Design-system sources not found next to the test binaries.");
        Assert.Empty(audit!.FindUndocumentedKeys());
    }
}
