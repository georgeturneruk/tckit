using TcKit.Core.Models;
using Xunit;

namespace TcKit.Tests;

public class SmokeTests
{
    [Fact]
    public void SymbolValue_HoldsItsFields()
    {
        var value = new SymbolValue("MAIN.x", "INT", "42");

        Assert.Equal("MAIN.x", value.InstancePath);
        Assert.Equal("INT", value.TypeName);
        Assert.Equal("42", value.Value);
    }
}
