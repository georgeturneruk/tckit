using System.Reflection;
using TcKit.Adapters.Analysis;
using TcKit.Adapters.Xml;
using TcKit.Core.Models;

namespace TcKit.Tests;

/// <summary>
/// The catalogue is what SARIF advertises as the tool's rule set, so a rule that fires without an
/// entry would be reported against a rule GitHub has never heard of. These tests pin the catalogue
/// to the engines rather than to a hand-written list, so adding a rule and forgetting to catalogue
/// it fails here instead of shipping.
/// </summary>
public class RuleCatalogueTests
{
    /// <summary>
    /// Every <c>TCKnnnn</c> const on either engine, found by reflection. A new rule id declared the
    /// way every existing one is declared is picked up without anyone remembering to list it.
    /// </summary>
    public static TheoryData<string> DeclaredRuleIds()
    {
        var data = new TheoryData<string>();
        foreach (var id in RuleIdsOn(typeof(NamingRuleEngine)).Concat(RuleIdsOn(typeof(CorrectnessRules))))
        {
            data.Add(id);
        }

        return data;
    }

    private static IEnumerable<string> RuleIdsOn(Type type)
        => type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Where(value => value.StartsWith("TCK", StringComparison.Ordinal) && value.Length == 7);

    [Theory]
    [MemberData(nameof(DeclaredRuleIds))]
    public void Catalogue_CoversEveryRuleIdTheEnginesDeclare(string ruleId)
        => Assert.NotNull(RuleCatalogue.Find(ruleId));

    [Fact]
    public void Catalogue_HasNoEntryForARuleThatDoesNotExist()
    {
        var declared = RuleIdsOn(typeof(NamingRuleEngine))
            .Concat(RuleIdsOn(typeof(CorrectnessRules)))
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(RuleCatalogue.All, rule => Assert.Contains(rule.Id, declared));
    }

    [Fact]
    public void Catalogue_IdsAreUniqueAndWellFormed()
    {
        Assert.Equal(
            RuleCatalogue.All.Count,
            RuleCatalogue.All.Select(rule => rule.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        Assert.All(RuleCatalogue.All, rule =>
        {
            Assert.Matches("^TCK[0-9]{4}$", rule.Id);
            Assert.NotEqual("", rule.Title);
            Assert.NotEqual("", rule.Description);
            Assert.StartsWith(RuleCatalogue.DocsPage, rule.HelpUri, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Catalogue_CategoriesAreTheThreeTheConfigurationKnows()
    {
        // A category typo would produce a rule nobody could turn down as a group, since
        // `tckit_analyzer_diagnostic.category-<name>.severity` matches on this string.
        string[] known =
        [
            RuleCatalogue.NamingCategory,
            RuleCatalogue.CorrectnessCategory,
            RuleCatalogue.StructureCategory,
        ];

        Assert.All(RuleCatalogue.All, rule => Assert.Contains(rule.Category, known));
    }

    /// <summary>
    /// The catalogue's category has to be the one findings actually carry, or configuration that
    /// turns a category down would miss the rules the docs say it covers.
    /// </summary>
    [Fact]
    public async Task Catalogue_AgreesWithTheCategoryEveryRealFindingCarries()
    {
        var result = await new ProjectAnalyser(new XmlProjectReader()).AnalyseAsync(
            new AnalysisRequest
            {
                ProjectPath = Fixtures.T3Solution,
                MinimumSeverity = DiagnosticSeverity.Suggestion,
            },
            CancellationToken.None);

        Assert.NotEmpty(result.Findings);
        Assert.All(result.Findings, finding =>
        {
            var rule = RuleCatalogue.Find(finding.RuleId);
            Assert.NotNull(rule);
            Assert.Equal(rule.Category, finding.Category);
        });

        // More than one category should be represented, or this would pass on a single-rule run.
        Assert.True(result.Findings.Select(f => f.Category).Distinct().Count() > 1);
    }

    /// <summary>
    /// A helpUri is a promise: GitHub renders it as the "View rule" link beside every alert, and a
    /// broken one is a dead end at exactly the moment someone wants to understand a finding.
    /// Anchors are checked against the headings of the page they point into, so moving or renaming
    /// a section fails here rather than quietly breaking the links.
    /// </summary>
    [Fact]
    public void Catalogue_AnchorsResolveToHeadingsOnTheDocumentationPage()
    {
        var page = Path.Combine(
            RepoRoot(), "docs", "content", "capabilities", "analysis", "overview.md");
        Assert.True(File.Exists(page), $"documentation page not found at {page}");

        var slugs = File.ReadLines(page)
            .Where(line => line.StartsWith('#'))
            .Select(line => Slug(line.TrimStart('#').Trim()))
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(RuleCatalogue.All, rule =>
            Assert.True(
                slugs.Contains(rule.Anchor),
                $"{rule.Id} points at #{rule.Anchor}, which is not a heading on {page}"));
    }

    /// <summary>Lowercase, non-alphanumerics to hyphens: the slug MkDocs generates for a heading.</summary>
    private static string Slug(string heading)
        => new(heading.ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray());

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
    {
        var testsDir = Path.GetDirectoryName(thisFile)!;
        return Directory.GetParent(testsDir)!.Parent!.Parent!.FullName;
    }

    [Fact]
    public void For_ReturnsOnlyTheRulesTheFindingsUse_InIdOrder()
    {
        AnalysisFinding Finding(string ruleId) => new()
        {
            RuleId = ruleId,
            Category = "correctness",
            Severity = DiagnosticSeverity.Warning,
            Message = "m",
            PlcName = "Plc",
            ObjectName = "FB_A",
            Line = 1,
            Symbol = "x",
        };

        var rules = RuleCatalogue.For(
        [
            Finding(CorrectnessRules.RealEqualityId),
            Finding(NamingRuleEngine.VariableRuleId),
            Finding(CorrectnessRules.RealEqualityId),
        ]);

        Assert.Equal(
            [NamingRuleEngine.VariableRuleId, CorrectnessRules.RealEqualityId],
            rules.Select(rule => rule.Id).ToArray());
    }

    [Fact]
    public void For_SkipsAnIdTheCatalogueDoesNotKnow()
    {
        // Better to advertise one rule fewer than to emit a SARIF run whose results reference a
        // rule its own driver never declared.
        var rules = RuleCatalogue.For(
        [
            new AnalysisFinding
            {
                RuleId = "TCK9999",
                Category = "correctness",
                Severity = DiagnosticSeverity.Warning,
                Message = "m",
                PlcName = "Plc",
                ObjectName = "FB_A",
                Line = 1,
                Symbol = "x",
            },
        ]);

        Assert.Empty(rules);
    }
}
