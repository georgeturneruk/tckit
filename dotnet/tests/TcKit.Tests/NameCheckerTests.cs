using TcKit.Adapters.Analysis;

namespace TcKit.Tests;

/// <summary>Tests name conformance checking and the advisory suggestions offered alongside findings.</summary>
public class NameCheckerTests
{
    private static NamingStyle Style(Capitalisation capitalisation, string prefix = "", string suffix = "")
        => new()
        {
            Name = "test",
            Capitalisation = capitalisation,
            RequiredPrefix = prefix,
            RequiredSuffix = suffix,
        };

    [Theory]
    [InlineData("Motor", Capitalisation.PascalCase, "", true)]
    [InlineData("motor", Capitalisation.PascalCase, "", false)]
    [InlineData("retryCount", Capitalisation.CamelCase, "", true)]
    [InlineData("RetryCount", Capitalisation.CamelCase, "", false)]
    [InlineData("FB_Motor", Capitalisation.PascalCase, "FB_", true)]
    [InlineData("Motor", Capitalisation.PascalCase, "FB_", false)]
    [InlineData("fb_Motor", Capitalisation.PascalCase, "FB_", false)]
    [InlineData("_state", Capitalisation.CamelCase, "_", true)]
    [InlineData("state", Capitalisation.CamelCase, "_", false)]
    [InlineData("MAX_LIMIT", Capitalisation.AllUpper, "", true)]
    public void Conforms_MatchesPrefixAndCapitalisation(
        string name, Capitalisation capitalisation, string prefix, bool expected)
        => Assert.Equal(expected, NameChecker.Conforms(name, Style(capitalisation, prefix)));

    [Fact]
    public void Conforms_UnderscoreInsideCore_FailsPascalCase()
    {
        // This is what makes the dotnet profile reject FB_Motor: the prefix is not required, so the
        // underscore is just an underscore in the middle of a name.
        Assert.False(NameChecker.Conforms("FB_Motor", Style(Capitalisation.PascalCase)));
    }

    [Fact]
    public void Conforms_PrefixOnly_IsNotAName()
        => Assert.False(NameChecker.Conforms("FB_", Style(Capitalisation.PascalCase, "FB_")));

    [Theory]
    [InlineData("Motor", "FB_", "FB_Motor")]
    [InlineData("fb_Motor", "FB_", "FB_Motor")]
    [InlineData("FB_Motor", "FB_", "FB_Motor")]
    public void Suggest_ObjectPrefix_IsAppliedWithoutDoubling(string name, string prefix, string expected)
        => Assert.Equal(expected, NameChecker.Suggest(name, Style(Capitalisation.PascalCase, prefix)));

    [Theory]
    [InlineData("bEnable", TypeClass.Bool, "Enable")]
    [InlineData("nRetryCount", TypeClass.Integer, "RetryCount")]
    [InlineData("fbInner", TypeClass.FbInstance, "Inner")]
    [InlineData("Enable", TypeClass.Bool, "Enable")]
    public void Suggest_StripsHungarianPrefixBeforeRecasing(
        string name, TypeClass typeClass, string expected)
        => Assert.Equal(expected, NameChecker.Suggest(name, Style(Capitalisation.PascalCase), typeClass));

    [Theory]
    [InlineData("strSuite", TypeClass.FbInstance, "StrSuite")]
    [InlineData("strName", TypeClass.String, "Name")]
    [InlineData("nCount", TypeClass.Unknown, "NCount")]
    public void Suggest_OnlyStripsAPrefixThatAgreesWithTheDeclaredType(
        string name, TypeClass typeClass, string expected)
    {
        // "strSuite : FB_StringTests" is a function block, so "str" is part of the word and must
        // survive; an unclassifiable type never loses a prefix either.
        Assert.Equal(expected, NameChecker.Suggest(name, Style(Capitalisation.PascalCase), typeClass));
    }

    [Theory]
    [InlineData("nState", TypeClass.Integer, "_state")]
    [InlineData("State", TypeClass.Integer, "_state")]
    [InlineData("_state", TypeClass.Integer, "_state")]
    public void Suggest_PrivateFieldStyle_ProducesUnderscoreCamelCase(
        string name, TypeClass typeClass, string expected)
        => Assert.Equal(expected, NameChecker.Suggest(name, Style(Capitalisation.CamelCase, "_"), typeClass));

    [Fact]
    public void Suggest_SingleLetterPrefix_DoesNotEatTheFirstWord()
    {
        // "Buffer" with a required prefix of "b" must become "bBuffer", not "bUffer".
        Assert.Equal("bBuffer", NameChecker.Suggest("Buffer", Style(Capitalisation.PascalCase, "b")));
    }

    [Theory]
    [InlineData("FB_Motor", "", "Motor")]
    [InlineData("GVL_Parameters", "", "Parameters")]
    [InlineData("FB_State", "E_", "E_State")]
    public void Suggest_StripsAnObjectKindPrefixRatherThanAbsorbingIt(
        string name, string prefix, string expected)
        => Assert.Equal(expected, NameChecker.Suggest(name, Style(Capitalisation.PascalCase, prefix)));

    [Fact]
    public void Suggest_KeepsAcronymsIntact()
        => Assert.Equal("ErrorID", NameChecker.Suggest("errorID", Style(Capitalisation.PascalCase)));

    [Fact]
    public void Suggest_SnakeCaseInput_IsRecasedByWord()
        => Assert.Equal("MaxRetryCount", NameChecker.Suggest("max_retry_count", Style(Capitalisation.PascalCase)));

    [Fact]
    public void Suggest_AllUpper_JoinsWordsWithUnderscores()
        => Assert.Equal("MAX_RETRIES", NameChecker.Suggest("maxRetries", Style(Capitalisation.AllUpper)));
}
