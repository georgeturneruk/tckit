using TcKit.Core.Models;

namespace TcKit.Core.Ports;

/// <summary>
/// Run TcUnit tests against a target runtime and parse the xUnit XML the run writes. Both methods
/// take the target AMS Net ID explicitly (no implicit "last deployed" state) plus an optional
/// plcName. See ADR-0005 / ADR-0011.
/// </summary>
public interface ITestRunner
{
    /// <summary>
    /// Ensure Run mode, then poll the TcUnit runner's AllTestSuitesFinished flag over ADS until the
    /// suites finish or the timeout fires; wait for the fresh xUnit XML and (when waitForResults)
    /// inline the failures-only results.
    /// </summary>
    Task<TestRunResult> RunTestsAsync(
        string targetAmsId, string? plcName, bool waitForResults, int timeoutSeconds,
        CancellationToken cancellationToken);

    /// <summary>Parse the full TcUnit results (passes included) from the published xUnit XML.</summary>
    Task<TestResults> GetResultsAsync(
        string targetAmsId, string? plcName, string? xmlPath, CancellationToken cancellationToken);
}
