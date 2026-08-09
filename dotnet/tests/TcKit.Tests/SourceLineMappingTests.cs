using TcKit.Adapters.Analysis;
using TcKit.Adapters.Reader;
using TcKit.Core.Models;

namespace TcKit.Tests;

/// <summary>
/// The declarations and bodies the reader returns are lifted out of CDATA sections, so a line
/// number within one of them means nothing on its own. These tests pin the offset that turns it
/// into a line of the file, which is what SARIF and any editor annotation hang on.
///
/// They are written to be drift-proof: rather than asserting a hard-coded line number that would
/// need updating whenever a fixture gains a variable, each one reads the file back off disk and
/// checks that the line at the recorded offset really is the line the reader handed over. A wrong
/// offset cannot pass, and an edited fixture cannot make a right one fail.
/// </summary>
public class SourceLineMappingTests
{
    [Fact]
    public async Task PouSource_DeclarationAndBodyLines_PointAtTheirFirstLineInTheFile()
    {
        await AssertEveryOffsetLandsOnItsOwnFirstLine(Fixtures.SampleProject);
    }

    [Fact]
    public async Task PouSource_LineOffsets_HoldForAWholeSolution()
    {
        await AssertEveryOffsetLandsOnItsOwnFirstLine(Fixtures.T3Solution);
    }

    /// <summary>
    /// CRLF is what TwinCAT itself writes, but the in-repo fixtures are LF, so a mapping that
    /// counted bytes rather than lines would pass every other test here. This copies a fixture to
    /// CRLF and runs the same assertions over it.
    /// </summary>
    [Fact]
    public async Task PouSource_LineOffsets_AreUnaffectedByCrlfLineEndings()
    {
        var crlf = CopyWithLineEndings(Fixtures.SampleProject, "\r\n");
        try
        {
            await AssertEveryOffsetLandsOnItsOwnFirstLine(crlf);
        }
        finally
        {
            Directory.Delete(crlf, recursive: true);
        }
    }

    [Fact]
    public async Task PouSource_DeclarationOpenedByANewline_SkipsPastItRatherThanReportingTheCdataLine()
    {
        // FB_Example writes "<![CDATA[" and then starts the code on the next line. Naively taking
        // the CDATA node's own line would put every finding in the declaration one line too high.
        var reader = await Primed(Fixtures.SampleProject);
        var pou = await reader.GetPouSourceAsync("FB_Example", null, CancellationToken.None);
        var lines = File.ReadAllLines(pou.Path);

        var cdataLine = Array.FindIndex(lines, line => line.Contains("<Declaration><![CDATA[")) + 1;
        Assert.True(cdataLine > 0, "fixture no longer opens its declaration with a CDATA section");
        Assert.EndsWith("<![CDATA[", lines[cdataLine - 1].TrimEnd(), StringComparison.Ordinal);
        Assert.Equal(cdataLine + 1, pou.DeclarationLine);
    }

    [Fact]
    public async Task Gvl_And_Dut_CarryTheirDeclarationLine()
    {
        var reader = await Primed(Fixtures.SampleProject);

        var gvl = await reader.GetGvlAsync("GVL_Params", null, CancellationToken.None);
        AssertFirstLineMatches(gvl.Path, gvl.DeclarationLine, gvl.Declaration, "GVL_Params declaration");

        var dut = await reader.GetDutAsync("ST_ExampleConfig", null, CancellationToken.None);
        AssertFirstLineMatches(dut.Path, dut.DeclarationLine, dut.Declaration, "ST_ExampleConfig declaration");
    }

    [Fact]
    public async Task PouSource_PropertyAccessor_IsLocatedSeparatelyFromItsHeader()
    {
        // A property is three members sharing one name, and the accessors are what hold code, so
        // they have to resolve to their own lines rather than all pointing at the header.
        var reader = await Primed(Fixtures.SampleProject);
        var pou = await reader.GetPouSourceAsync("FB_Example", null, CancellationToken.None);

        var header = Assert.Single(pou.Members, m => m.Name == "ErrorId" && m.Kind == PouMemberKind.Property);
        var getter = Assert.Single(pou.Members, m => m.Name == "ErrorId.Get");
        var setter = Assert.Single(pou.Members, m => m.Name == "ErrorId.Set");

        AssertFirstLineMatches(pou.Path, header.DeclarationLine, header.Declaration, "ErrorId header");
        AssertFirstLineMatches(pou.Path, getter.BodyLine, getter.Body, "ErrorId.Get body");
        AssertFirstLineMatches(pou.Path, setter.DeclarationLine, setter.Declaration, "ErrorId.Set declaration");

        // A getter with no locals is written as an empty declaration block, which is the normal
        // shape rather than an edge case. It has no first line, so it gets no line.
        Assert.Equal("", getter.Declaration);
        Assert.Equal(0, getter.DeclarationLine);

        Assert.True(getter.BodyLine > header.DeclarationLine);
        Assert.True(setter.DeclarationLine > getter.BodyLine);
    }

    [Fact]
    public async Task PouSource_EmptyBody_ReportsNoLineRatherThanGuessingOne()
    {
        // An interface method has a declaration and no implementation at all. Reporting line 1 of
        // the file for it would be worse than admitting the location is unknown.
        var reader = await Primed(Fixtures.SampleProject);
        var pou = await reader.GetPouSourceAsync("I_Example", null, CancellationToken.None);

        var method = pou.Members.First(m => m.Kind == PouMemberKind.Method);
        Assert.Equal("", method.Body);
        Assert.Equal(0, method.BodyLine);
        Assert.True(method.DeclarationLine > 0);
    }

    /// <summary>
    /// The end of the lane the rest of this class covers piecewise: a real analysis run over a real
    /// solution, where every finding's file and line is checked to land on a line that actually
    /// contains the identifier the finding is about.
    ///
    /// This is the assertion SARIF depends on. A finding that annotates the wrong line is worse
    /// than one that annotates nothing, because it sends a reader to innocent code.
    /// </summary>
    [Fact]
    public async Task AnalyseAsync_EveryFinding_LandsOnALineContainingItsSymbol()
    {
        var analyser = new ProjectAnalyser(new XmlProjectReader());
        var result = await analyser.AnalyseAsync(
            new AnalysisRequest
            {
                ProjectPath = Fixtures.T3Solution,
                MinimumSeverity = DiagnosticSeverity.Suggestion,
            },
            CancellationToken.None);

        Assert.NotEmpty(result.Findings);
        var located = 0;

        foreach (var finding in result.Findings)
        {
            if (finding.FileLine == 0)
            {
                Assert.Equal("", finding.FilePath);
                continue;
            }

            Assert.True(File.Exists(finding.FilePath), $"{finding.RuleId}: no such file {finding.FilePath}");
            var lines = File.ReadAllLines(finding.FilePath);
            Assert.True(
                finding.FileLine <= lines.Length,
                $"{finding.RuleId} on {finding.Symbol} reports line {finding.FileLine} of a "
                + $"{lines.Length}-line file");

            Assert.Contains(
                finding.Symbol,
                lines[finding.FileLine - 1],
                StringComparison.OrdinalIgnoreCase);
            located++;
        }

        Assert.True(located > 5, $"only {located} findings resolved to a file line");
    }

    /// <summary>
    /// The multi-project fixture declares an <c>E_State</c> in each of its two PLC projects. A
    /// location keyed on the object name alone would resolve both to whichever file was indexed
    /// last, quietly attributing one project's findings to the other's source.
    ///
    /// Rather than approximate, this compares each finding's path against the path the reader
    /// itself recorded for that PLC and object, so it holds however the fixture is laid out.
    /// </summary>
    [Fact]
    public async Task AnalyseAsync_SameNamedObjectsInTwoPlcs_ResolveToTheirOwnFiles()
    {
        var reader = new XmlProjectReader();
        var result = await new ProjectAnalyser(reader).AnalyseAsync(
            new AnalysisRequest
            {
                ProjectPath = Fixtures.MultiProject,
                MinimumSeverity = DiagnosticSeverity.Suggestion,
            },
            CancellationToken.None);

        var structure = await reader.GetStructureAsync(
            Fixtures.MultiProject, null, CancellationToken.None);

        var paths = new Dictionary<(string Plc, string Object), string>();
        foreach (var plc in structure.Plcs.Values)
        {
            foreach (var pou in plc.Pous)
            {
                paths[(plc.Name, pou.Name)] = pou.Path;
            }

            foreach (var gvl in plc.Gvls)
            {
                paths[(plc.Name, gvl.Name)] = gvl.Path;
            }

            foreach (var dut in plc.Duts)
            {
                paths[(plc.Name, dut.Name)] = dut.Path;
            }
        }

        var shared = paths.Keys.GroupBy(key => key.Object).Where(group => group.Count() > 1).ToList();
        Assert.True(shared.Count > 0, "fixture no longer declares one object name in two PLC projects");

        var located = result.Findings.Where(finding => finding.FileLine > 0).ToList();
        Assert.True(
            located.Select(finding => finding.PlcName).Distinct().Count() > 1,
            "no findings resolved in more than one PLC project");

        Assert.All(located, finding =>
            Assert.Equal(paths[(finding.PlcName, finding.ObjectName)], finding.FilePath));
    }

    private static async Task<XmlProjectReader> Primed(string projectPath)
    {
        var reader = new XmlProjectReader();
        await reader.GetStructureAsync(projectPath, null, CancellationToken.None);
        return reader;
    }

    /// <summary>
    /// Walk every POU, GVL and DUT of a project and check each recorded offset against the file.
    /// Blocks with no line (an absent body) are the one thing not asserted on, and the count of
    /// blocks actually checked is asserted to be non-trivial so a reader that returned nothing
    /// could not pass by vacuous truth.
    /// </summary>
    private static async Task AssertEveryOffsetLandsOnItsOwnFirstLine(string projectPath)
    {
        var reader = await Primed(projectPath);
        var structure = await reader.GetStructureAsync(projectPath, null, CancellationToken.None);
        var checkedBlocks = 0;

        foreach (var plc in structure.Plcs.Values)
        {
            foreach (var pouRef in plc.Pous)
            {
                var pou = await reader.GetPouSourceAsync(pouRef.Name, plc.Name, CancellationToken.None);
                checkedBlocks += Check(pou.Path, pou.DeclarationLine, pou.Declaration, $"{pou.PouName} declaration");
                checkedBlocks += Check(pou.Path, pou.BodyLine, pou.Body, $"{pou.PouName} body");

                foreach (var member in pou.Members)
                {
                    var where = $"{pou.PouName}.{member.Name}";
                    checkedBlocks += Check(pou.Path, member.DeclarationLine, member.Declaration, $"{where} declaration");
                    checkedBlocks += Check(pou.Path, member.BodyLine, member.Body, $"{where} body");
                }
            }

            foreach (var gvlRef in plc.Gvls)
            {
                var gvl = await reader.GetGvlAsync(gvlRef.Name, plc.Name, CancellationToken.None);
                checkedBlocks += Check(gvl.Path, gvl.DeclarationLine, gvl.Declaration, $"{gvl.Name} declaration");
            }

            foreach (var dutRef in plc.Duts)
            {
                var dut = await reader.GetDutAsync(dutRef.Name, plc.Name, CancellationToken.None);
                checkedBlocks += Check(dut.Path, dut.DeclarationLine, dut.Declaration, $"{dut.Name} declaration");
            }
        }

        Assert.True(checkedBlocks > 10, $"only {checkedBlocks} source blocks were checked in {projectPath}");
    }

    private static int Check(string path, int line, string text, string what)
    {
        if (text.Length == 0)
        {
            Assert.Equal(0, line);
            return 0;
        }

        AssertFirstLineMatches(path, line, text, what);
        return 1;
    }

    private static void AssertFirstLineMatches(string path, int line, string text, string what)
    {
        Assert.True(line > 0, $"{what} has no line offset");
        var lines = File.ReadAllLines(path);
        Assert.True(line <= lines.Length, $"{what} reports line {line}, past the end of {path}");

        // Contains rather than equals, because a block whose CDATA opens inline shares its first
        // line with the surrounding XML: `<Declaration><![CDATA[PROGRAM MAIN`.
        var expected = text.Split('\n')[0].TrimEnd('\r');
        Assert.Contains(expected, lines[line - 1], StringComparison.Ordinal);
    }

    /// <summary>Copy a fixture tree into a temp directory, rewriting every text file's line endings.</summary>
    private static string CopyWithLineEndings(string source, string ending)
    {
        var destination = Path.Combine(Path.GetTempPath(), $"tckit-lines-{Guid.NewGuid():N}");
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            var text = File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal);
            File.WriteAllText(target, text.Replace("\n", ending, StringComparison.Ordinal));
        }

        return destination;
    }
}
