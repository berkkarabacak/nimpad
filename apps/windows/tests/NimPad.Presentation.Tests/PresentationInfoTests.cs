using NimPad.Presentation;
using Xunit;

namespace NimPad.Presentation.Tests;

public class PresentationInfoTests
{
    [Fact]
    public void ProductNameFlowsFromCore() => Assert.Equal("NimPad", PresentationInfo.ProductName);
}
