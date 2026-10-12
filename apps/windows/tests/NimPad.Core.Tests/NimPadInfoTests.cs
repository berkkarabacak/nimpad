using NimPad.Core;
using Xunit;

namespace NimPad.Core.Tests;

public class NimPadInfoTests
{
    [Fact]
    public void ProductNameIsNimPad() => Assert.Equal("NimPad", NimPadInfo.ProductName);
}
