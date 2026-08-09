using TcKit.Core.Authoring;
using TcKit.Core.Models;

namespace TcKit.Tests;

/// <summary>Tests the domain-enum to Automation-Interface kind-integer maps.</summary>
public class TcKindTests
{
    [Theory]
    [InlineData(PouType.Program, 602)]
    [InlineData(PouType.Function, 603)]
    [InlineData(PouType.FunctionBlock, 604)]
    [InlineData(PouType.Interface, 618)]
    public void ForPou_MapsToTwinCatKind(PouType pouType, int expected)
        => Assert.Equal(expected, TcKind.ForPou(pouType));

    [Theory]
    [InlineData(DutKind.Enum, 605)]
    [InlineData(DutKind.Struct, 606)]
    [InlineData(DutKind.Union, 607)]
    public void ForDut_MapsToTwinCatKind(DutKind dutKind, int expected)
        => Assert.Equal(expected, TcKind.ForDut(dutKind));

    [Fact]
    public void ForDut_Alias_IsNotSupported()
        => Assert.Throws<NotSupportedException>(() => TcKind.ForDut(DutKind.Alias));
}
