using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using TcKit.Core.Models;

namespace TcKit.Adapters.Ads;

/// <summary>
/// Parse a TcUnit JUnit-style XML results file into the structured TestResults shape. Summary totals
/// always reflect the full run; the suites list narrows to failures-only when requested (run_tests
/// inline view), but the counts do not. Pure (no ADS / COM), so it is unit-tested against fixtures.
/// Port of ConvertFrom-TcUnitXml in bridge/harness/_TcUnit.psm1.
/// </summary>
internal static partial class TcUnitXml
{
    public sealed record Parsed(
        bool Success,
        string? Error,
        IReadOnlyList<TestSuiteResult> Suites,
        TestSummary Summary,
        IReadOnlyList<FlatFailure> Failures,
        string XmlPath);

    public static Parsed Parse(string xmlPath, bool failuresOnly)
    {
        if (!File.Exists(xmlPath))
        {
            return Fail($"TcUnit results XML not found at {xmlPath}.", xmlPath);
        }

        XmlDocument doc;
        try
        {
            doc = new XmlDocument();
            doc.Load(xmlPath);
        }
        catch (XmlException ex)
        {
            return Fail(ex.Message, xmlPath);
        }

        var root = doc.DocumentElement;
        if (root is null)
        {
            return Fail($"TcUnit results XML at {xmlPath} has no root element.", xmlPath);
        }

        List<XmlElement> suiteNodes;
        if (root.LocalName == "testsuites")
        {
            suiteNodes = root.SelectNodes("testsuite")?.OfType<XmlElement>().ToList() ?? [];
        }
        else if (root.LocalName == "testsuite")
        {
            suiteNodes = [root];
        }
        else
        {
            return Fail(
                $"Unexpected root element '{root.LocalName}' in {xmlPath} (expected testsuites or testsuite).",
                xmlPath);
        }

        // Build the full tree first so summary totals cover the whole run, then narrow on the way out.
        var fullSuites = suiteNodes.Select(ParseSuite).ToList();
        var summarySource = root.LocalName == "testsuites" ? root : null;
        var summary = BuildSummary(summarySource, fullSuites);

        var failuresFlat = new List<FlatFailure>();
        foreach (var suite in fullSuites)
        {
            foreach (var test in suite.Tests.Where(t => !t.Passed))
            {
                var message = test.Failures.Count > 0 ? test.Failures[0].Message : "";
                failuresFlat.Add(new FlatFailure(suite.Name, test.Name, message));
            }
        }

        IReadOnlyList<TestSuiteResult> suitesOut = fullSuites;
        if (failuresOnly)
        {
            suitesOut = fullSuites
                .Select(s => s with { Tests = s.Tests.Where(t => !t.Passed).ToList() })
                .Where(s => s.Tests.Count > 0)
                .ToList();
        }

        return new Parsed(true, null, suitesOut, summary, failuresFlat, xmlPath);
    }

    private static TestSummary BuildSummary(XmlElement? summarySource, IReadOnlyList<TestSuiteResult> fullSuites)
    {
        var tests = 0;
        var failures = 0;
        var errors = 0;
        var duration = 0.0;
        if (summarySource is not null)
        {
            tests = IntAttr(summarySource, "tests");
            failures = IntAttr(summarySource, "failures");
            errors = IntAttr(summarySource, "errors");
            duration = DoubleAttr(summarySource, "time");
        }

        if (tests == 0)
        {
            tests = fullSuites.Sum(s => s.Tests.Count);
        }

        var asserts = fullSuites.Sum(s => s.Tests.Sum(t => t.Asserts));
        if (failures == 0)
        {
            failures = fullSuites.Sum(s => s.Tests.Count(t => !t.Passed));
        }

        return new TestSummary
        {
            Suites = fullSuites.Count,
            Tests = tests,
            Asserts = asserts,
            Failures = failures,
            Errors = errors,
            DurationSeconds = duration,
        };
    }

    private static TestSuiteResult ParseSuite(XmlElement node)
    {
        var tests = node.ChildNodes
            .OfType<XmlElement>()
            .Where(c => c.LocalName == "testcase")
            .Select(ParseCase)
            .ToList();
        return new TestSuiteResult { Name = node.GetAttribute("name"), Tests = tests };
    }

    private static TestCaseResult ParseCase(XmlElement node)
    {
        double? duration = null;
        if (double.TryParse(node.GetAttribute("time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
        {
            duration = t;
        }

        var failures = node.ChildNodes
            .OfType<XmlElement>()
            .Where(c => c.LocalName is "failure" or "error")
            .Select(ParseFailure)
            .ToList();

        var asserts = IntAttr(node, "asserts");
        if (asserts == 0)
        {
            asserts = Math.Max(1, failures.Count);
        }

        return new TestCaseResult
        {
            Name = node.GetAttribute("name"),
            Passed = failures.Count == 0,
            Asserts = asserts,
            Failures = failures,
            DurationSeconds = duration,
        };
    }

    private static TestFailureDetail ParseFailure(XmlElement node)
    {
        var message = node.GetAttribute("message");
        var body = node.InnerText;
        if (string.IsNullOrEmpty(message))
        {
            message = body;
        }

        var expected = "";
        var actual = "";
        var line = 0;

        var expectedMatch = ExpectedPattern().Match(body);
        if (expectedMatch.Success)
        {
            expected = expectedMatch.Groups[1].Value.Trim();
        }

        var actualMatch = ActualPattern().Match(body);
        if (actualMatch.Success)
        {
            actual = actualMatch.Groups[1].Value.Trim();
        }

        var lineMatch = LinePattern().Match(body);
        if (lineMatch.Success)
        {
            line = int.Parse(lineMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        else
        {
            var parenMatch = ParenLinePattern().Match(body);
            if (parenMatch.Success)
            {
                line = int.Parse(parenMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            }
        }

        return new TestFailureDetail(message, expected, actual, line);
    }

    private static int IntAttr(XmlElement node, string name)
        => int.TryParse(node.GetAttribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static double DoubleAttr(XmlElement node, string name)
        => double.TryParse(node.GetAttribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0.0;

    private static Parsed Fail(string error, string xmlPath)
        => new(false, error, [], new TestSummary(), [], xmlPath);

    [GeneratedRegex(@"(?i)expected\s*[:=]?\s*['""]?([^\s,;'""]+)['""]?")]
    private static partial Regex ExpectedPattern();

    [GeneratedRegex(@"(?i)(?:but\s*was|actual)\s*[:=]?\s*['""]?([^\s,;'""]+)['""]?")]
    private static partial Regex ActualPattern();

    [GeneratedRegex(@"(?i)\bline\s*[:=]?\s*(\d+)")]
    private static partial Regex LinePattern();

    [GeneratedRegex(@"\((\d+)(?:,\d+)?\)\s*:")]
    private static partial Regex ParenLinePattern();
}
