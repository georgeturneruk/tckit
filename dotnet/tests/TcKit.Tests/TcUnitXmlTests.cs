using TcKit.Adapters.Ads;

namespace TcKit.Tests;

/// <summary>The TcUnit JUnit XML parser against fixtures: summary totals, suite/case tree, failure detail extraction.</summary>
public sealed class TcUnitXmlTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tckit-tcunit-" + Guid.NewGuid().ToString("N"));

    public TcUnitXmlTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone.
        }
    }

    private const string Sample =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <testsuites tests="3" failures="1" errors="0" time="0.5">
          <testsuite name="PRG_Suite1" tests="2" failures="1" errors="0" time="0.3">
            <testcase name="Test_Pass" classname="PRG_Suite1" time="0.1" />
            <testcase name="Test_Fail" classname="PRG_Suite1" time="0.2">
              <failure message="value mismatch" type="assertion">expected '5' but was '7' at line 12</failure>
            </testcase>
          </testsuite>
          <testsuite name="PRG_Suite2" tests="1" failures="0" errors="0" time="0.2">
            <testcase name="Test_Ok" classname="PRG_Suite2" time="0.2" />
          </testsuite>
        </testsuites>
        """;

    private string WriteXml(string body = Sample)
    {
        var path = Path.Combine(_dir, "results.xml");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void Parse_FullTree_BuildsSuitesAndSummary()
    {
        var parsed = TcUnitXml.Parse(WriteXml(), failuresOnly: false);

        Assert.True(parsed.Success);
        Assert.Equal(2, parsed.Suites.Count);
        Assert.Equal(2, parsed.Summary.Suites);
        Assert.Equal(3, parsed.Summary.Tests);
        Assert.Equal(1, parsed.Summary.Failures);
        Assert.Equal(0.5, parsed.Summary.DurationSeconds);

        var flat = Assert.Single(parsed.Failures);
        Assert.Equal("PRG_Suite1", flat.SuiteName);
        Assert.Equal("Test_Fail", flat.TestName);
        Assert.Equal("value mismatch", flat.Message);
    }

    [Fact]
    public void Parse_ExtractsExpectedActualLine()
    {
        var parsed = TcUnitXml.Parse(WriteXml(), failuresOnly: false);

        var failing = parsed.Suites.Single(s => s.Name == "PRG_Suite1").Tests.Single(t => !t.Passed);
        var detail = Assert.Single(failing.Failures);
        Assert.Equal("5", detail.Expected);
        Assert.Equal("7", detail.Actual);
        Assert.Equal(12, detail.Line);
    }

    [Fact]
    public void Parse_FailuresOnly_NarrowsSuitesButKeepsSummary()
    {
        var parsed = TcUnitXml.Parse(WriteXml(), failuresOnly: true);

        var suite = Assert.Single(parsed.Suites);
        Assert.Equal("PRG_Suite1", suite.Name);
        Assert.Single(suite.Tests);
        Assert.False(suite.Tests[0].Passed);
        // Summary still reflects the full run.
        Assert.Equal(3, parsed.Summary.Tests);
    }

    [Fact]
    public void Parse_MissingFile_ReturnsError()
    {
        var parsed = TcUnitXml.Parse(Path.Combine(_dir, "absent.xml"), failuresOnly: false);

        Assert.False(parsed.Success);
        Assert.Contains("not found", parsed.Error);
    }

    [Fact]
    public void Parse_UnexpectedRoot_ReturnsError()
    {
        var path = WriteXml("<root/>");
        var parsed = TcUnitXml.Parse(path, failuresOnly: false);

        Assert.False(parsed.Success);
        Assert.Contains("Unexpected root", parsed.Error);
    }
}
