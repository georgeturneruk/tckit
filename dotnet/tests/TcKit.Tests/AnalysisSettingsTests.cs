using TcKit.Adapters.Analysis;
using TcKit.Core.Analysis;
using TcKit.Core.Models;

namespace TcKit.Tests;

/// <summary>Tests the .editorconfig-shaped configuration: profiles, custom rules, and severity overrides.</summary>
public class AnalysisSettingsTests
{
    private static AnalysisSettings Load(params (string Key, string Value)[] properties)
        => AnalysisSettingsLoader.FromProperties(
            properties.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void FromProperties_NoConfiguration_DefaultsToHybrid()
    {
        var settings = Load();

        Assert.Equal(NamingProfiles.Hybrid, settings.Profile);
        Assert.NotEmpty(settings.Rules);
        Assert.Empty(settings.ConfigWarnings);
    }

    [Fact]
    public void FromProperties_NamedProfile_IsHonoured()
        => Assert.Equal(
            NamingProfiles.Hungarian,
            Load(("tckit_analysis_profile", "hungarian")).Profile);

    [Fact]
    public void FromProperties_UnknownProfile_WarnsAndFallsBack()
    {
        var settings = Load(("tckit_analysis_profile", "klingon"));

        Assert.Equal(NamingProfiles.Hybrid, settings.Profile);
        Assert.Contains(settings.ConfigWarnings, warning => warning.Contains("klingon", StringComparison.Ordinal));
    }

    [Fact]
    public void FromProperties_CustomRule_IsBuiltFromItsGroupAndStyle()
    {
        var settings = Load(
            ("tckit_naming_symbols.globals.applicable_kinds", "variable"),
            ("tckit_naming_symbols.globals.applicable_sections", "var_global"),
            ("tckit_naming_style.screaming.capitalization", "all_upper"),
            ("tckit_naming_rule.globals_screaming.symbols", "globals"),
            ("tckit_naming_rule.globals_screaming.style", "screaming"),
            ("tckit_naming_rule.globals_screaming.severity", "warning"));

        var rule = Assert.Single(settings.Rules, candidate => candidate.Name == "globals_screaming");
        Assert.Equal(DiagnosticSeverity.Warning, rule.Severity);
        Assert.Equal(Capitalisation.AllUpper, rule.Style.Capitalisation);
        Assert.Equal([SymbolKind.Variable], rule.Symbols.Kinds);
        Assert.Equal([VarSection.VarGlobal], rule.Symbols.Sections);
    }

    [Fact]
    public void FromProperties_RuleWithUndefinedStyle_IsSkippedWithAWarning()
    {
        var settings = Load(
            ("tckit_naming_symbols.globals.applicable_kinds", "variable"),
            ("tckit_naming_rule.broken.symbols", "globals"),
            ("tckit_naming_rule.broken.style", "does_not_exist"));

        Assert.DoesNotContain(settings.Rules, rule => rule.Name == "broken");
        Assert.Contains(settings.ConfigWarnings, warning => warning.Contains("does_not_exist", StringComparison.Ordinal));
    }

    [Fact]
    public void FromProperties_VarConstantSection_MeansVarPlusTheModifier()
    {
        var settings = Load(
            ("tckit_naming_symbols.consts.applicable_kinds", "variable"),
            ("tckit_naming_symbols.consts.applicable_sections", "var_constant"),
            ("tckit_naming_style.upper.capitalization", "all_upper"),
            ("tckit_naming_rule.consts_upper.symbols", "consts"),
            ("tckit_naming_rule.consts_upper.style", "upper"));

        var rule = Assert.Single(settings.Rules, candidate => candidate.Name == "consts_upper");
        Assert.Contains(VarSection.Var, rule.Symbols.Sections);
        Assert.True(rule.Symbols.RequiredModifiers.HasFlag(VarQualifiers.Constant));
    }

    [Fact]
    public void FromProperties_RulesAreOrderedMostSpecificFirst()
    {
        var settings = Load(("tckit_analysis_profile", "hybrid"));

        var specificities = settings.Rules.Select(rule => rule.Symbols.Specificity).ToList();
        Assert.Equal(specificities.OrderByDescending(value => value), specificities);
    }

    [Fact]
    public void SeverityFor_RuleIdOverride_BeatsCategoryAndDefault()
    {
        var settings = Load(
            ("tckit_diagnostic.TCK1002.severity", "error"),
            ("tckit_analyzer_diagnostic.category-naming.severity", "warning"));

        Assert.Equal(
            DiagnosticSeverity.Error,
            settings.SeverityFor("TCK1002", "naming", DiagnosticSeverity.Suggestion));
    }

    [Fact]
    public void SeverityFor_CategoryOverride_BeatsTheRuleDefault()
    {
        var settings = Load(("tckit_analyzer_diagnostic.category-naming.severity", "none"));

        Assert.Equal(
            DiagnosticSeverity.None,
            settings.SeverityFor("TCK1001", "naming", DiagnosticSeverity.Suggestion));
    }

    [Fact]
    public void Run_SeverityNone_SuppressesTheFinding()
    {
        var settings = Load(("tckit_analyzer_diagnostic.category-naming.severity", "none"));
        var symbol = new NamedSymbol
        {
            Name = "motor",
            Kind = SymbolKind.FunctionBlock,
            PlcName = "Plc",
            ObjectName = "motor",
            Line = 1,
        };

        Assert.Empty(NamingRuleEngine.Run([symbol], settings));
    }
}
