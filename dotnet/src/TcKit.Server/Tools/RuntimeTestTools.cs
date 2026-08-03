using System.ComponentModel;
using ModelContextProtocol.Server;
using TcKit.Core.Ports;
using TcKit.Core.Security;
using TcKit.Core.Serialization;

namespace TcKit.Server.Tools;

/// <summary>
/// Runtime control + TcUnit test tools (ADS). They target a runtime by AMS Net ID; no XAE needed.
/// run_tests ensures Run mode, polls the TcUnit runner, and inlines failures-only results; the full
/// per-test list (passes included) comes from GetTestResults. StartRuntime and RunTests act on a live
/// target (execute-class, NetId-gated); GetTestResults just parses the published XML (read-class).
/// </summary>
[McpServerToolType]
public sealed class RuntimeTestTools(IRuntimeControl runtime, ITestRunner tests, IPermissionGate gate)
{
    [McpServerTool(Name = "StartRuntime")]
    [Description("Restart the target into Run mode over ADS (WriteControl on the system service), "
        + "waiting until it is reached. targetAmsId is the target's AMS Net ID.")]
    public Task<string> StartRuntime(string targetAmsId, CancellationToken cancellationToken = default)
        => Run(PermissionLevel.Execute, targetAmsId, () => runtime.StartRuntimeAsync(targetAmsId, cancellationToken));

    [McpServerTool(Name = "RunTests")]
    [Description("Run the TcUnit suites on a target to completion and return the outcome. Ensures Run "
        + "mode, polls AllTestSuitesFinished, then (when waitForResults, default true) inlines the "
        + "failures-only results. timeoutSeconds bounds the wait (default 120). In the result, "
        + "'success' means the run infrastructure worked; 'tests_passed' is the test outcome "
        + "(false on assertion failures or when expected results never appeared; null with "
        + "waitForResults=false). Check tests_passed, not success, to judge the tests.")]
    public Task<string> RunTests(
        string targetAmsId, bool waitForResults = true, int timeoutSeconds = 120,
        string plcName = "", CancellationToken cancellationToken = default)
        => Run(PermissionLevel.Execute, targetAmsId,
            () => tests.RunTestsAsync(targetAmsId, Optional(plcName), waitForResults, timeoutSeconds, cancellationToken));

    [McpServerTool(Name = "GetTestResults")]
    [Description("Parse the full TcUnit results (passes included) from the published xUnit XML. The "
        + "default path resolves from targetAmsId: a user-mode runtime declaring that Net ID owns "
        + "its boot-folder XML. xmlPath overrides the resolved default (set it when the project "
        + "overrides xUnitFilePath).")]
    public Task<string> GetTestResults(
        string targetAmsId, string plcName = "", string xmlPath = "", CancellationToken cancellationToken = default)
        => Run(PermissionLevel.Read, null, () => tests.GetResultsAsync(targetAmsId, Optional(plcName), Optional(xmlPath), cancellationToken));

    private static string? Optional(string value) => string.IsNullOrEmpty(value) ? null : value;

    private async Task<string> Run<T>(PermissionLevel level, string? targetAmsId, Func<Task<T>> call)
    {
        var denied = gate.Deny(level, targetAmsId);
        if (denied is not null)
        {
            return TckitJson.Serialize(new { error = denied });
        }

        try
        {
            return TckitJson.Serialize(await call().ConfigureAwait(false));
        }
#pragma warning disable CA1031 // The tool boundary funnels every failure into the error contract.
        catch (Exception exc)
        {
            return TckitJson.Serialize(new { error = exc.Message });
        }
#pragma warning restore CA1031
    }
}
