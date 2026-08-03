using TcKit.Core.Models;
using TcKit.Core.Ports;

namespace TcKit.Core.Workflows;

/// <summary>
/// Outcome of the composite CI test run. <see cref="Success"/> is infrastructure-only (every stage
/// ran to completion); <see cref="TestsPassed"/> is the test outcome per the TestRunResult
/// contract. <see cref="FailedStage"/> names the stage that stopped the run (null when all ran).
/// </summary>
public sealed record TestWorkflowResult
{
    public required bool Success { get; init; }
    public bool? TestsPassed { get; init; }
    public string? FailedStage { get; init; }
    public Result? Open { get; init; }
    public BuildResult? Build { get; init; }
    public Result? Deploy { get; init; }
    public TestRunResult? Tests { get; init; }

    /// <summary>Where the xUnit results were copied when a junit output path was requested.</summary>
    public string? JunitPath { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// The composite CI verb (TASKS task 7): open (attach) -> build -> deploy (autostart) -> run tests
/// (which ensures Run mode and carries the licence preflight) -> copy the xUnit results. One call,
/// one trustworthy exit-code mapping: infrastructure failure at any stage stops the run; the test
/// outcome rides in <see cref="TestWorkflowResult.TestsPassed"/>.
/// </summary>
public static class TestWorkflow
{
    public static async Task<TestWorkflowResult> RunAsync(
        IProjectWriter writer, IBuildRunner builder, ITestRunner tests,
        string solutionPath, string? plcName, string targetAmsId, int timeoutSeconds, string? junitPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(solutionPath))
        {
            throw new ArgumentException("Solution path required.");
        }

        if (string.IsNullOrEmpty(targetAmsId))
        {
            throw new ArgumentException("TargetAmsId required.");
        }

        var open = await writer.OpenProjectAsync(solutionPath, cancellationToken).ConfigureAwait(false);
        if (!open.Success)
        {
            return Failed("open", open.Error) with { Open = open };
        }

        var build = await builder.BuildAsync(plcName, forceLog: false, cancellationToken).ConfigureAwait(false);
        if (!build.Success)
        {
            return Failed("build", build.Error) with { Open = open, Build = build };
        }

        var deploy = await builder
            .DeployAsync(targetAmsId, plcName, bootAutostart: true, cancellationToken).ConfigureAwait(false);
        if (!deploy.Success)
        {
            return Failed("deploy", deploy.Error) with { Open = open, Build = build, Deploy = deploy };
        }

        var run = await tests
            .RunTestsAsync(targetAmsId, plcName, waitForResults: true, timeoutSeconds, cancellationToken)
            .ConfigureAwait(false);

        string? copiedJunit = null;
        if (run.Success && run.XmlPublished && !string.IsNullOrEmpty(junitPath))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(junitPath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Copy(run.XmlPath, junitPath, overwrite: true);
            copiedJunit = Path.GetFullPath(junitPath);
        }

        return new TestWorkflowResult
        {
            Success = run.Success,
            TestsPassed = run.TestsPassed,
            FailedStage = run.Success ? null : "tests",
            Open = open,
            Build = build,
            Deploy = deploy,
            Tests = run,
            JunitPath = copiedJunit,
            Error = run.Error,
        };
    }

    private static TestWorkflowResult Failed(string stage, string? error) => new()
    {
        Success = false,
        FailedStage = stage,
        Error = error,
    };
}
