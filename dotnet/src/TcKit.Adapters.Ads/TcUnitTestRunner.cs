using TcKit.Core.Models;
using TcKit.Core.Ports;

namespace TcKit.Adapters.Ads;

/// <summary>
/// ADS <see cref="ITestRunner"/>: runs a TcUnit cycle (ensure Run mode, poll the runner, wait for
/// the xUnit XML) and parses results. Thin shell over <see cref="RuntimeOperations"/> + <see cref="TcUnitXml"/>
/// (both unit-tested without a live runtime); failures map to the result error contracts.
/// </summary>
public sealed class TcUnitTestRunner : ITestRunner
{
    private readonly IAdsFactory _factory;

    public TcUnitTestRunner()
        : this(new AdsFactory())
    {
    }

    internal TcUnitTestRunner(IAdsFactory factory) => _factory = factory;

    public Task<TestRunResult> RunTestsAsync(
        string targetAmsId, string? plcName, bool waitForResults, int timeoutSeconds,
        CancellationToken cancellationToken)
        => Task.Run(
            () =>
            {
                try
                {
                    return RuntimeOperations.RunTests(_factory, targetAmsId, plcName, waitForResults, timeoutSeconds);
                }
#pragma warning disable CA1031 // The test boundary funnels every failure into the result error contract.
                catch (Exception ex)
                {
                    return new TestRunResult { Success = false, Error = ex.Message };
                }
#pragma warning restore CA1031
            },
            cancellationToken);

    public Task<TestResults> GetResultsAsync(
        string targetAmsId, string? plcName, string? xmlPath, CancellationToken cancellationToken)
        => Task.Run(
            () =>
            {
                try
                {
                    return RuntimeOperations.GetResults(plcName, xmlPath, targetAmsId);
                }
#pragma warning disable CA1031 // The test boundary funnels every failure into the result error contract.
                catch (Exception ex)
                {
                    return new TestResults { Success = false, Error = ex.Message };
                }
#pragma warning restore CA1031
            },
            cancellationToken);
}
