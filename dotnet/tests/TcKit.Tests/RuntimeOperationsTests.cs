using TcKit.Adapters.Ads;

namespace TcKit.Tests;

/// <summary>
/// start_runtime / run_tests / get_test_results orchestration against the fake ADS seam: state
/// transitions, the suites-finished poll, XML-published inlining, timeout, and result parsing.
/// </summary>
public sealed class RuntimeOperationsTests : IDisposable
{
    private const string FinishedSymbol = "GVL_TcUnit.TcUnitRunner.AllTestSuitesFinished";
    private const string SuiteCountSymbol = "GVL_TcUnit.NumberOfInitializedTestSuites";

    private const string Sample =
        """
        <testsuites tests="2" failures="1" errors="0" time="0.4">
          <testsuite name="PRG_Suite" tests="2" failures="1" errors="0" time="0.4">
            <testcase name="Test_Pass" time="0.1" />
            <testcase name="Test_Fail" time="0.3"><failure message="boom">expected '1' but was '2'</failure></testcase>
          </testsuite>
        </testsuites>
        """;

    private const string SampleAllPass =
        """
        <testsuites tests="2" failures="0" errors="0" time="0.4">
          <testsuite name="PRG_Suite" tests="2" failures="0" errors="0" time="0.4">
            <testcase name="Test_A" time="0.1" />
            <testcase name="Test_B" time="0.3" />
          </testsuite>
        </testsuites>
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tckit-runtime-" + Guid.NewGuid().ToString("N"));

    public RuntimeOperationsTests() => Directory.CreateDirectory(_dir);

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

    [Fact]
    public void StartRuntime_Succeeds_WhenRunModeReached()
    {
        var factory = new FakeAdsFactory();

        var result = RuntimeOperations.StartRuntime(factory, "1.2.3.4.1.1");

        Assert.True(result.Success);
        Assert.Equal(TcSystemState.Run, factory.System.Requested);
        Assert.Equal("Run", result.Details["mode"]);
    }

    [Fact]
    public void StartRuntime_Fails_WhenStateNotReached()
    {
        var factory = new FakeAdsFactory();
        factory.System.Reachable = false;

        var result = RuntimeOperations.StartRuntime(factory, "1.2.3.4.1.1");

        Assert.False(result.Success);
        Assert.Contains("Run mode", result.Error);
    }

    [Fact]
    public void StartRuntime_EmptyTarget_Throws()
        => Assert.Throws<ArgumentException>(() => RuntimeOperations.StartRuntime(new FakeAdsFactory(), ""));

    [Fact]
    public void StartRuntime_StuckInConfig_AppendsLicenceDiagnosis()
    {
        var factory = new FakeAdsFactory
        {
            LicenceDiagnosis = "The target's trial licence 'TC1200' expired on 2026-06-30.",
        };
        factory.System.Reachable = false;
        factory.System.FinalOnFailure = "Config";

        var result = RuntimeOperations.StartRuntime(factory, "1.2.3.4.1.1");

        Assert.False(result.Success);
        Assert.Contains("Run mode", result.Error);
        Assert.Contains("trial licence 'TC1200' expired", result.Error);
    }

    [Fact]
    public void StartRuntime_StuckElsewhere_NoLicenceLookup()
    {
        var factory = new FakeAdsFactory { LicenceDiagnosis = "should not appear" };
        factory.System.Reachable = false; // FinalOnFailure defaults to "Stop"

        var result = RuntimeOperations.StartRuntime(factory, "1.2.3.4.1.1");

        Assert.False(result.Success);
        Assert.DoesNotContain("should not appear", result.Error);
    }

    [Fact]
    public void RunTests_FinishedAndPublished_InlinesFailures()
    {
        var xmlPath = Path.Combine(_dir, "results.xml");
        var plc = new FakePlcSymbols(
            bools: new() { [FinishedSymbol] = true },
            ints: new() { [SuiteCountSymbol] = 1 })
        {
            OnFinished = () =>
            {
                File.WriteAllText(xmlPath, Sample);
                // Guarantee mtime is strictly after the run's start timestamp.
                File.SetLastWriteTimeUtc(xmlPath, DateTime.UtcNow.AddSeconds(2));
            },
        };
        var factory = new FakeAdsFactory(plc);

        var result = RuntimeOperations.RunTests(
            factory, "1.2.3.4.1.1", plcName: null, waitForResults: true, timeoutSeconds: 5,
            pollIntervalMs: 1, xmlPathOverride: xmlPath);

        Assert.True(result.Success);
        Assert.True(result.XmlPublished);
        Assert.True(result.ResultsIncluded);
        Assert.Single(result.Failures);
        Assert.Equal("Test_Fail", result.Failures[0].TestName);
        Assert.False(result.TestsPassed); // run infrastructure succeeded, but an assertion failed
        Assert.True(plc.Disposed);
    }

    [Fact]
    public void RunTests_AllPass_ReportsTestsPassed()
    {
        var xmlPath = Path.Combine(_dir, "results.xml");
        var plc = new FakePlcSymbols(
            bools: new() { [FinishedSymbol] = true },
            ints: new() { [SuiteCountSymbol] = 1 })
        {
            OnFinished = () =>
            {
                File.WriteAllText(xmlPath, SampleAllPass);
                File.SetLastWriteTimeUtc(xmlPath, DateTime.UtcNow.AddSeconds(2));
            },
        };
        var factory = new FakeAdsFactory(plc);

        var result = RuntimeOperations.RunTests(
            factory, "1.2.3.4.1.1", plcName: null, waitForResults: true, timeoutSeconds: 5,
            pollIntervalMs: 1, xmlPathOverride: xmlPath);

        Assert.True(result.Success);
        Assert.True(result.TestsPassed);
    }

    [Fact]
    public void RunTests_NoWait_LeavesOutcomeUnknown()
    {
        var plc = new FakePlcSymbols(
            bools: new() { [FinishedSymbol] = true },
            ints: new() { [SuiteCountSymbol] = 1 });
        var factory = new FakeAdsFactory(plc);

        var result = RuntimeOperations.RunTests(
            factory, "1.2.3.4.1.1", plcName: null, waitForResults: false, timeoutSeconds: 5,
            pollIntervalMs: 1, xmlPathOverride: Path.Combine(_dir, "unpublished.xml"), xmlFreshTimeoutMs: 10);

        Assert.True(result.Success);
        Assert.Null(result.TestsPassed);
    }

    [Fact]
    public void RunTests_ResultsExpectedButNeverPublished_FailsOutcome()
    {
        var plc = new FakePlcSymbols(
            bools: new() { [FinishedSymbol] = true },
            ints: new() { [SuiteCountSymbol] = 1 });
        var factory = new FakeAdsFactory(plc);

        var result = RuntimeOperations.RunTests(
            factory, "1.2.3.4.1.1", plcName: null, waitForResults: true, timeoutSeconds: 5,
            pollIntervalMs: 1, xmlPathOverride: Path.Combine(_dir, "never-written.xml"), xmlFreshTimeoutMs: 10);

        Assert.True(result.Success);
        Assert.False(result.XmlPublished);
        Assert.False(result.TestsPassed);
    }

    [Fact]
    public void RunTests_Timeout_WhenSuitesNeverFinish()
    {
        var plc = new FakePlcSymbols(bools: new() { [FinishedSymbol] = false });
        var factory = new FakeAdsFactory(plc);

        var result = RuntimeOperations.RunTests(
            factory, "1.2.3.4.1.1", plcName: null, waitForResults: true, timeoutSeconds: 0,
            pollIntervalMs: 1, xmlPathOverride: Path.Combine(_dir, "none.xml"));

        Assert.False(result.Success);
        Assert.Contains("did not finish", result.Error);
    }

    [Fact]
    public void RunTests_Fails_WhenRuntimeNotReachable()
    {
        var factory = new FakeAdsFactory();
        factory.System.Reachable = false;

        var result = RuntimeOperations.RunTests(
            factory, "1.2.3.4.1.1", plcName: null, waitForResults: true, timeoutSeconds: 5,
            pollIntervalMs: 1, xmlPathOverride: Path.Combine(_dir, "none.xml"));

        Assert.False(result.Success);
        Assert.Contains("Run mode", result.Error);
    }

    [Fact]
    public void RunTests_StuckInConfig_AppendsLicenceDiagnosis()
    {
        var factory = new FakeAdsFactory { LicenceDiagnosis = "expired trial licence" };
        factory.System.Reachable = false;
        factory.System.FinalOnFailure = "Config";

        var result = RuntimeOperations.RunTests(
            factory, "1.2.3.4.1.1", plcName: null, waitForResults: true, timeoutSeconds: 5,
            pollIntervalMs: 1, xmlPathOverride: Path.Combine(_dir, "none.xml"));

        Assert.False(result.Success);
        Assert.Contains("expired trial licence", result.Error);
    }

    [Fact]
    public void GetResults_ParsesFullTree()
    {
        var xmlPath = Path.Combine(_dir, "full.xml");
        File.WriteAllText(xmlPath, Sample);

        var results = RuntimeOperations.GetResults(plcName: null, xmlPath);

        Assert.True(results.Success);
        var suite = Assert.Single(results.Suites);
        Assert.Equal(2, suite.Tests.Count); // passes included
        Assert.Equal(1, results.Summary.Failures);
        Assert.False(results.TestsPassed);
    }

    [Fact]
    public void GetResults_AllPass_ReportsTestsPassed()
    {
        var xmlPath = Path.Combine(_dir, "pass.xml");
        File.WriteAllText(xmlPath, SampleAllPass);

        var results = RuntimeOperations.GetResults(plcName: null, xmlPath);

        Assert.True(results.Success);
        Assert.True(results.TestsPassed);
    }

    [Fact]
    public void GetResults_MissingFile_ReturnsError()
    {
        var results = RuntimeOperations.GetResults(plcName: null, Path.Combine(_dir, "absent.xml"));

        Assert.False(results.Success);
        Assert.Contains("not found", results.Error);
        Assert.Null(results.TestsPassed);
    }
}
