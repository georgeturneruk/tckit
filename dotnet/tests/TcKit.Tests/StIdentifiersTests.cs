using TcKit.Core.Analysis;

namespace TcKit.Tests;

/// <summary>Tests identifier scanning over masked ST, where word boundaries decide correctness.</summary>
public class StIdentifiersTests
{
    [Theory]
    [InlineData("count := 1;", "count", true)]
    [InlineData("COUNT := 1;", "count", true)]
    [InlineData("counter := 1;", "count", false)]
    [InlineData("nCount := 1;", "count", false)]
    [InlineData("fb.count := 1;", "count", false)]
    [InlineData("IF count > 0 THEN", "count", true)]
    public void Mentions_MatchesWholeIdentifiersOnly(string masked, string name, bool expected)
        => Assert.Equal(expected, StIdentifiers.Mentions(masked, name));

    [Fact]
    public void Mentions_IgnoresAQualifiedUseOfTheSameName()
    {
        // "GVL_State.Mode" is a use of GVL_State, not of a local called Mode.
        Assert.False(StIdentifiers.Mentions("GVL_State.Mode := 1;", "Mode"));
        Assert.True(StIdentifiers.Mentions("GVL_State.Mode := 1;", "GVL_State"));
    }

    [Theory]
    [InlineData("total := total + 1;", "total", true)]
    [InlineData("IF total = 1 THEN", "total", false)]
    [InlineData("other := 1;", "total", false)]
    public void IsAssigned_LooksForTheAssignmentOperator(string masked, string name, bool expected)
        => Assert.Equal(expected, StIdentifiers.IsAssigned(masked, name));

    [Theory]
    [InlineData("GVL_State.Mode := 1;", true)]
    [InlineData("GVL_State . Mode := 1;", true)]
    [InlineData("IF GVL_State.Mode = 1 THEN", false)]
    [InlineData("Other.Mode := 1;", false)]
    [InlineData("GVL_State.Other := 1;", false)]
    public void AssignsMember_MatchesQualifiedWritesOnly(string masked, bool expected)
        => Assert.Equal(expected, StIdentifiers.AssignsMember(masked, "GVL_State", "Mode"));

    [Theory]
    [InlineData("IF a = b THEN", "=")]
    [InlineData("IF a <> b THEN", "<>")]
    public void Comparisons_FindsEqualityOperators(string masked, string expectedOperator)
    {
        var comparison = Assert.Single(StIdentifiers.Comparisons(masked));

        Assert.Equal("a", comparison.Left);
        Assert.Equal("b", comparison.Right);
        Assert.Equal(expectedOperator, comparison.Operator);
    }

    [Theory]
    [InlineData("a := b;")]
    [InlineData("IF a >= b THEN")]
    [InlineData("IF a <= b THEN")]
    public void Comparisons_ExcludesAssignmentAndOrderingOperators(string masked)
        => Assert.Empty(StIdentifiers.Comparisons(masked));

    [Fact]
    public void Comparisons_ReportsTheOffsetSoALineCanBeDerived()
    {
        var masked = "n := 1;\nIF a = b THEN\n    ;\nEND_IF";

        var comparison = Assert.Single(StIdentifiers.Comparisons(masked));

        Assert.Equal(2, StSource.LineAt(masked, comparison.Index));
    }

    [Theory]
    [InlineData("1.5", true)]
    [InlineData("0.0", true)]
    [InlineData("5", false)]
    [InlineData("speed", false)]
    public void IsRealLiteral_RecognisesFloatingPointLiterals(string token, bool expected)
        => Assert.Equal(expected, StIdentifiers.IsRealLiteral(token));
}
