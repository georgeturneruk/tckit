using System.Diagnostics;
using TcKit.Core.Models;

namespace TcKit.Adapters.Ads;

/// <summary>
/// start_runtime / run_tests / get_test_results orchestration against the <see cref="IAdsFactory"/>
/// seam, so the flow is testable against a fake without a live runtime. The ADS specifics live in
/// the native factory. Mirrors Invoke-TcRuntime.ps1 / Invoke-TcUnitRun.ps1 / Get-TcUnitResults.ps1.
/// </summary>
internal static class RuntimeOperations
{
    private const string FinishedSymbol = "GVL_TcUnit.TcUnitRunner.AllTestSuitesFinished";
    private const string SuiteCountSymbol = "GVL_TcUnit.NumberOfInitializedTestSuites";
    private const int RunModeTimeoutMs = 30000;
    private const int XmlFreshTimeoutMs = 5000;

    public static Result StartRuntime(IAdsFactory factory, string targetAmsId)
    {
        if (string.IsNullOrEmpty(targetAmsId))
        {
            throw new ArgumentException("TargetAmsId required.");
        }

        var state = factory.OpenSystem(targetAmsId).SetState(TcSystemState.Run, RunModeTimeoutMs);
        if (!state.Reached)
        {
            return Result.Fail($"Runtime did not reach Run mode on {targetAmsId} (final state '{state.Final}').");
        }

        return Result.Ok(new Dictionary<string, object?>
        {
            ["target"] = targetAmsId,
            ["mode"] = "Run",
            ["original"] = state.Original,
            ["reached"] = state.Final,
            ["latency_ms"] = state.LatencyMs,
        });
    }

    public static TestRunResult RunTests(
        IAdsFactory factory, string targetAmsId, string? plcName, bool waitForResults, int timeoutSeconds,
        int pollIntervalMs = 500, string? xmlPathOverride = null)
    {
        if (string.IsNullOrEmpty(targetAmsId))
        {
            throw new ArgumentException("TargetAmsId required.");
        }

        var (xmlPath, resolveWarning) = ResolvePath(xmlPathOverride);

        // Stale-XML mitigation: delete the prior file and record the start so we can detect the new write.
        if (File.Exists(xmlPath))
        {
            File.Delete(xmlPath);
        }

        var start = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        var runState = factory.OpenSystem(targetAmsId).SetState(TcSystemState.Run, RunModeTimeoutMs);
        if (!runState.Reached)
        {
            return FailedRun($"Runtime did not reach Run mode: final state '{runState.Final}'.", xmlPath, resolveWarning);
        }

        using var plc = factory.OpenPlc(targetAmsId, TcUnitPaths.DefaultPlcPort);

        var deadline = TimeSpan.FromSeconds(timeoutSeconds);
        var finished = false;
        while (stopwatch.Elapsed < deadline)
        {
            if (plc.TryReadBool(FinishedSymbol, out var done) && done)
            {
                finished = true;
                break;
            }

            Thread.Sleep(pollIntervalMs);
        }

        if (!finished)
        {
            return FailedRun(
                $"Tests did not finish within {timeoutSeconds}s (AllTestSuitesFinished still false).",
                xmlPath, resolveWarning);
        }

        // The publisher only writes XML when xUnitEnablePublish is overridden TRUE; tolerate its absence.
        var xmlPublished = WaitFileFresh(xmlPath, start, XmlFreshTimeoutMs);
        var suiteCount = plc.TryReadInt(SuiteCountSymbol, out var suites) ? suites : 0;

        var summary = new TestSummary { Suites = suiteCount, DurationSeconds = stopwatch.Elapsed.TotalSeconds };
        IReadOnlyList<TestSuiteResult> suitesOut = [];
        IReadOnlyList<FlatFailure> failuresOut = [];
        var resultsIncluded = false;

        if (waitForResults && xmlPublished)
        {
            var parsed = TcUnitXml.Parse(xmlPath, failuresOnly: true);
            if (parsed.Success)
            {
                summary = parsed.Summary with { DurationSeconds = stopwatch.Elapsed.TotalSeconds };
                suitesOut = parsed.Suites;
                failuresOut = parsed.Failures;
                resultsIncluded = true;
            }
        }

        return new TestRunResult
        {
            Success = true,
            DurationSeconds = stopwatch.Elapsed.TotalSeconds,
            Summary = summary,
            XmlPath = xmlPath,
            XmlPublished = xmlPublished,
            ResultsIncluded = resultsIncluded,
            Suites = suitesOut,
            Failures = failuresOut,
            ResolveWarning = resolveWarning,
        };
    }

    public static TestResults GetResults(string? plcName, string? xmlPath)
    {
        var (resolvedPath, resolveWarning) = ResolvePath(xmlPath);
        var parsed = TcUnitXml.Parse(resolvedPath, failuresOnly: false);
        return new TestResults
        {
            Success = parsed.Success,
            Suites = parsed.Suites,
            Summary = parsed.Summary,
            Failures = parsed.Failures,
            XmlPath = parsed.XmlPath,
            ResolveWarning = resolveWarning,
            Error = parsed.Error,
        };
    }

    private static (string Path, string Warning) ResolvePath(string? xmlPathOverride)
        => string.IsNullOrEmpty(xmlPathOverride) ? TcUnitPaths.ResolveDefault() : (xmlPathOverride, "");

    private static bool WaitFileFresh(string path, DateTime after, int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path) > after)
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return File.Exists(path) && File.GetLastWriteTimeUtc(path) > after;
    }

    private static TestRunResult FailedRun(string error, string xmlPath, string resolveWarning) => new()
    {
        Success = false,
        Error = error,
        XmlPath = xmlPath,
        ResolveWarning = resolveWarning,
    };
}
