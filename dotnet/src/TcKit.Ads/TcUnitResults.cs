using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TcKit.Ads;

/// <summary>
/// TcUnit results: resolve the path the xUnit publisher writes to, and parse the file.
///
/// Path resolution mirrors GVL_Param_TcUnit.xUnitFilePath (default
/// '%TC_BOOTPRJPATH%tcunit_xunit_testresults.xml'), whose expansion is runtime-kind dependent: on a
/// user-mode runtime it is the Boot root of that runtime's install; on a 4026 local runtime it is
/// under C:\ProgramData\Beckhoff\TwinCAT\3.1\Boot; on a pre-4026 kernel RT it is under
/// C:\TwinCAT\3.1\Boot. Resolution order: TCKIT_TCUNIT_XML_PATH env override -> the boot folder of
/// the UmRT whose TcRegistry.xml declares the target's AMS Net ID (existence not required: a test
/// run resolves before the file is written) -> existing local/kernel-RT file (4026 ProgramData boot
/// before legacy kernel boot, Port_&lt;port&gt; subfolder before Boot root) -> freshest existing UmRT
/// candidate -> the Port_&lt;port&gt; path under whichever boot root exists on disk (4026 ProgramData
/// preferred) as a stable fallback.
///
/// Parsing is pure (no ADS), JUnit-style XML in, <see cref="TcUnitParsed"/> out; summary totals
/// always reflect the full run even when the suites list is narrowed to failures-only.
/// </summary>
public static partial class TcUnitResults
{
    /// <summary>The standard first-PLC runtime port.</summary>
    public const int DefaultPlcPort = 851;

    private const string XmlFileName = "tcunit_xunit_testresults.xml";

    /// <summary>
    /// Resolve the path TcUnit's xUnit publisher writes results to for <paramref name="targetAmsId"/>.
    /// The warning is non-empty when the choice was ambiguous (multiple UmRT candidates).
    /// </summary>
    public static (string Path, string Warning) ResolveDefaultPath(
        string? targetAmsId = null, int port = DefaultPlcPort)
        => ResolveDefaultPath(
            targetAmsId,
            port,
            Environment.GetEnvironmentVariable("TCKIT_TCUNIT_XML_PATH"),
            @"C:\TwinCAT\3.1\Boot",
            LocalBootDir(),
            RuntimesRoot());

    internal static (string Path, string Warning) ResolveDefaultPath(
        string? targetAmsId, int port, string? envOverride, string kernelBootDir,
        string? localBootDir, string? runtimesRoot)
    {
        if (!string.IsNullOrEmpty(envOverride))
        {
            return (envOverride, "");
        }

        // Target-aware resolution: the UmRT that owns the target's AMS Net ID owns the result path,
        // regardless of what stale files exist elsewhere.
        if (!string.IsNullOrEmpty(targetAmsId) && runtimesRoot is not null)
        {
            var bootDir = FindRuntimeBootDir(runtimesRoot, targetAmsId);
            if (bootDir is not null)
            {
                return (Path.Combine(bootDir, XmlFileName), "");
            }
        }

        // Local/kernel RT: the 4026 local runtime boots from ProgramData, pre-4026 kernel RT from
        // C:\TwinCAT; the Port_<port> subfolder is the historic default and the Boot root is where
        // %TC_BOOTPRJPATH% lands on user-mode runtimes and possibly on kernel targets too, so an
        // existing file in any of the four counts, 4026 first.
        var bootRoots = new List<string>(2);
        if (localBootDir is not null)
        {
            bootRoots.Add(localBootDir);
        }

        bootRoots.Add(kernelBootDir);
        foreach (var bootRoot in bootRoots)
        {
            var portPath = Path.Combine(bootRoot, "Plc", $"Port_{port}", XmlFileName);
            if (File.Exists(portPath))
            {
                return (portPath, "");
            }

            var rootPath = Path.Combine(bootRoot, XmlFileName);
            if (File.Exists(rootPath))
            {
                return (rootPath, "");
            }
        }

        if (runtimesRoot is not null && Directory.Exists(runtimesRoot))
        {
            var marker = $"{Path.DirectorySeparatorChar}3.1{Path.DirectorySeparatorChar}Boot{Path.DirectorySeparatorChar}";
            var candidates = Directory
                .EnumerateFiles(runtimesRoot, XmlFileName, SearchOption.AllDirectories)
                .Where(p => p.Contains(marker, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();

            if (candidates.Count == 1)
            {
                return (candidates[0], "");
            }

            if (candidates.Count > 1)
            {
                var others = string.Join("; ", candidates.Skip(1));
                return (candidates[0],
                    "Multiple UmRT runtimes published TcUnit XML; using freshest. "
                    + $"Set TCKIT_TCUNIT_XML_PATH to pin. Alternatives: {others}");
            }
        }

        // Nothing exists yet (a test run resolves before the file is written): fall back to the
        // Port_<port> path under the boot root that is actually on disk, 4026 ProgramData first.
        var fallbackRoot = localBootDir is not null && Directory.Exists(localBootDir)
            ? localBootDir
            : kernelBootDir;
        return (Path.Combine(fallbackRoot, "Plc", $"Port_{port}", XmlFileName), "");
    }

    /// <summary>Parse a TcUnit JUnit-style results file. Never throws; failures land in the result.</summary>
    public static TcUnitParsed Parse(string xmlPath, bool failuresOnly)
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

        var failuresFlat = new List<TcUnitFlatFailure>();
        foreach (var suite in fullSuites)
        {
            foreach (var test in suite.Tests.Where(t => !t.Passed))
            {
                var message = test.Failures.Count > 0 ? test.Failures[0].Message : "";
                failuresFlat.Add(new TcUnitFlatFailure(suite.Name, test.Name, message));
            }
        }

        IReadOnlyList<TcUnitSuite> suitesOut = fullSuites;
        if (failuresOnly)
        {
            suitesOut = fullSuites
                .Select(s => s with { Tests = s.Tests.Where(t => !t.Passed).ToList() })
                .Where(s => s.Tests.Count > 0)
                .ToList();
        }

        return new TcUnitParsed
        {
            Success = true,
            Suites = suitesOut,
            Summary = summary,
            Failures = failuresFlat,
            XmlPath = xmlPath,
        };
    }

    private static string? RuntimesRoot()
    {
        var programData = Environment.GetEnvironmentVariable("ProgramData");
        return string.IsNullOrEmpty(programData)
            ? null
            : Path.Combine(programData, "Beckhoff", "TwinCAT", "3.1", "Runtimes");
    }

    /// <summary>The 4026 local runtime's boot root (%TC_BOOTPRJPATH% lives under ProgramData).</summary>
    private static string? LocalBootDir()
    {
        var programData = Environment.GetEnvironmentVariable("ProgramData");
        return string.IsNullOrEmpty(programData)
            ? null
            : Path.Combine(programData, "Beckhoff", "TwinCAT", "3.1", "Boot");
    }

    /// <summary>
    /// Find the boot folder of the user-mode runtime whose TcRegistry.xml declares
    /// <paramref name="targetAmsId"/>. The registry stores the Net ID as a 6-byte hex BIN value and
    /// the boot folder as the BootDir string (which may contain doubled separators).
    /// </summary>
    private static string? FindRuntimeBootDir(string runtimesRoot, string targetAmsId)
    {
        if (!Directory.Exists(runtimesRoot))
        {
            return null;
        }

        foreach (var runtimeDir in Directory.EnumerateDirectories(runtimesRoot))
        {
            var registryPath = Path.Combine(runtimeDir, "3.1", "TcRegistry.xml");
            if (!File.Exists(registryPath))
            {
                continue;
            }

            string? netIdHex;
            string? bootDir;
            try
            {
                var values = XDocument.Load(registryPath).Descendants("Value").ToList();
                netIdHex = values.FirstOrDefault(v => (string?)v.Attribute("Name") == "AmsNetId")?.Value;
                bootDir = values.FirstOrDefault(v => (string?)v.Attribute("Name") == "BootDir")?.Value;
            }
            catch (XmlException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (netIdHex is null || HexToDottedNetId(netIdHex) != targetAmsId)
            {
                continue;
            }

            return string.IsNullOrWhiteSpace(bootDir)
                ? Path.Combine(runtimeDir, "3.1", "Boot")
                : Path.GetFullPath(bootDir.Trim());
        }

        return null;
    }

    private static string? HexToDottedNetId(string hex)
    {
        hex = hex.Trim();
        if (hex.Length != 12)
        {
            return null;
        }

        var parts = new string[6];
        for (var i = 0; i < 6; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            parts[i] = value.ToString(CultureInfo.InvariantCulture);
        }

        return string.Join('.', parts);
    }

    private static TcUnitSummary BuildSummary(XmlElement? summarySource, IReadOnlyList<TcUnitSuite> fullSuites)
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

        return new TcUnitSummary
        {
            Suites = fullSuites.Count,
            Tests = tests,
            Asserts = asserts,
            Failures = failures,
            Errors = errors,
            DurationSeconds = duration,
        };
    }

    private static TcUnitSuite ParseSuite(XmlElement node)
    {
        var tests = node.ChildNodes
            .OfType<XmlElement>()
            .Where(c => c.LocalName == "testcase")
            .Select(ParseCase)
            .ToList();
        return new TcUnitSuite { Name = node.GetAttribute("name"), Tests = tests };
    }

    private static TcUnitCase ParseCase(XmlElement node)
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

        return new TcUnitCase
        {
            Name = node.GetAttribute("name"),
            Passed = failures.Count == 0,
            Asserts = asserts,
            Failures = failures,
            DurationSeconds = duration,
        };
    }

    private static TcUnitFailure ParseFailure(XmlElement node)
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

        return new TcUnitFailure(message, expected, actual, line);
    }

    private static int IntAttr(XmlElement node, string name)
        => int.TryParse(node.GetAttribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static double DoubleAttr(XmlElement node, string name)
        => double.TryParse(node.GetAttribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0.0;

    private static TcUnitParsed Fail(string error, string xmlPath)
        => new() { Success = false, Error = error, XmlPath = xmlPath };

    [GeneratedRegex(@"(?i)expected\s*[:=]?\s*['""]?([^\s,;'""]+)['""]?")]
    private static partial Regex ExpectedPattern();

    [GeneratedRegex(@"(?i)(?:but\s*was|actual)\s*[:=]?\s*['""]?([^\s,;'""]+)['""]?")]
    private static partial Regex ActualPattern();

    [GeneratedRegex(@"(?i)\bline\s*[:=]?\s*(\d+)")]
    private static partial Regex LinePattern();

    [GeneratedRegex(@"\((\d+)(?:,\d+)?\)\s*:")]
    private static partial Regex ParenLinePattern();
}
