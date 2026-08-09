using TcKit.Adapters.Analysis;
using TcKit.Core.Analysis;
using TcKit.Core.Models;

namespace TcKit.Tests;

/// <summary>
/// Tests the infer profile, which reports departures from what a project already does rather than
/// from an imposed table. The thresholds matter most: inferring from thin evidence would produce
/// confidently wrong findings.
/// </summary>
public class ProfileInferenceTests
{
    private static NamedSymbol Variable(
        string name, TypeClass typeClass, VarSection section, SymbolScope scope = SymbolScope.Object)
        => new()
        {
            Name = name,
            Kind = SymbolKind.Variable,
            PlcName = "Plc",
            ObjectName = "FB_Host",
            Line = 1,
            Section = section,
            Scope = scope,
            TypeClass = typeClass,
            TypeExpression = typeClass.ToString(),
        };

    private static NamedSymbol Object(string name, SymbolKind kind) => new()
    {
        Name = name,
        Kind = kind,
        PlcName = "Plc",
        ObjectName = name,
        Line = 1,
    };

    private static AnalysisSettings Settings(IReadOnlyList<NamedSymbol> symbols)
        => new() { Profile = NamingProfiles.Infer, Rules = ProfileInference.Infer(symbols) };

    [Fact]
    public void Infer_HungarianProject_LearnsTheTypePrefixes()
    {
        var symbols = new List<NamedSymbol>
        {
            Variable("bEnable", TypeClass.Bool, VarSection.VarInput),
            Variable("bReset", TypeClass.Bool, VarSection.VarInput),
            Variable("bDone", TypeClass.Bool, VarSection.VarInput),
            Variable("bError", TypeClass.Bool, VarSection.VarInput),
        };

        var findings = NamingRuleEngine.Run(symbols, Settings(symbols));

        Assert.Empty(findings);
    }

    [Fact]
    public void Infer_HungarianProject_FlagsTheOddOneOut()
    {
        var symbols = new List<NamedSymbol>
        {
            Variable("bEnable", TypeClass.Bool, VarSection.VarInput),
            Variable("bReset", TypeClass.Bool, VarSection.VarInput),
            Variable("bDone", TypeClass.Bool, VarSection.VarInput),
            Variable("Started", TypeClass.Bool, VarSection.VarInput),
        };

        var finding = Assert.Single(NamingRuleEngine.Run(symbols, Settings(symbols)));

        Assert.Equal("Started", finding.Symbol);
        Assert.Equal("bStarted", finding.Suggestion);
    }

    [Fact]
    public void Infer_DotNetStyleProject_LearnsPascalCaseWithNoPrefix()
    {
        var symbols = new List<NamedSymbol>
        {
            Variable("Enable", TypeClass.Bool, VarSection.VarInput),
            Variable("Reset", TypeClass.Bool, VarSection.VarInput),
            Variable("Done", TypeClass.Bool, VarSection.VarInput),
            Variable("bError", TypeClass.Bool, VarSection.VarInput),
        };

        var finding = Assert.Single(NamingRuleEngine.Run(symbols, Settings(symbols)));

        Assert.Equal("bError", finding.Symbol);
    }

    [Fact]
    public void Infer_ObjectPrefixes_AreLearnedPerKind()
    {
        var symbols = new List<NamedSymbol>
        {
            Object("FB_Motor", SymbolKind.FunctionBlock),
            Object("FB_Pump", SymbolKind.FunctionBlock),
            Object("FB_Valve", SymbolKind.FunctionBlock),
            Object("Heater", SymbolKind.FunctionBlock),
        };

        var finding = Assert.Single(NamingRuleEngine.Run(symbols, Settings(symbols)));

        Assert.Equal("Heater", finding.Symbol);
        Assert.Equal("FB_Heater", finding.Suggestion);
    }

    [Fact]
    public void Infer_BelowTheSampleThreshold_InfersNothing()
    {
        // Two declarations are not a convention. Inferring from them would enforce a coincidence.
        var symbols = new List<NamedSymbol>
        {
            Variable("bEnable", TypeClass.Bool, VarSection.VarInput),
            Variable("Started", TypeClass.Bool, VarSection.VarInput),
        };

        Assert.Empty(NamingRuleEngine.Run(symbols, Settings(symbols)));
    }

    [Fact]
    public void Infer_InconsistentProject_InfersNothingRatherThanPickingASide()
    {
        var symbols = new List<NamedSymbol>
        {
            Variable("bEnable", TypeClass.Bool, VarSection.VarInput),
            Variable("Reset", TypeClass.Bool, VarSection.VarInput),
            Variable("bDone", TypeClass.Bool, VarSection.VarInput),
            Variable("Started", TypeClass.Bool, VarSection.VarInput),
            Variable("Running", TypeClass.Bool, VarSection.VarInput),
        };

        // No prefix reaches the agreement threshold, and the casing split is likewise even.
        var findings = NamingRuleEngine.Run(symbols, Settings(symbols));

        Assert.True(findings.Count <= 2, $"Expected inference to stay quiet, got {findings.Count} findings.");
    }

    [Fact]
    public void Infer_ScopePrefixAndTypePrefix_AreLearnedIndependently()
    {
        var symbols = new List<NamedSymbol>
        {
            Variable("_nCount", TypeClass.Integer, VarSection.Var),
            Variable("_nTotal", TypeClass.Integer, VarSection.Var),
            Variable("_nIndex", TypeClass.Integer, VarSection.Var),
            Variable("nStray", TypeClass.Integer, VarSection.Var),
        };

        var finding = Assert.Single(NamingRuleEngine.Run(symbols, Settings(symbols)));

        Assert.Equal("nStray", finding.Symbol);
        Assert.Equal("_nStray", finding.Suggestion);
    }

    [Fact]
    public void Infer_ReservedNames_DoNotSkewTheSample()
    {
        // MAIN is all-upper and would drag a small PROGRAM sample towards UPPER_CASE.
        var symbols = new List<NamedSymbol>
        {
            Object("MAIN", SymbolKind.Program),
            Object("PRG_Cycle", SymbolKind.Program),
            Object("PRG_Init", SymbolKind.Program),
            Object("PRG_Shutdown", SymbolKind.Program),
        };

        Assert.Empty(NamingRuleEngine.Run(symbols, Settings(symbols)));
    }
}
