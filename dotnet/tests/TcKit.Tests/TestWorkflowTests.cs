using TcKit.Core.Models;
using TcKit.Core.Ports;
using TcKit.Core.Workflows;

namespace TcKit.Tests;

/// <summary>
/// The composite CI workflow against fake ports: stage ordering, short-circuit on the first
/// infrastructure failure, the junit copy, and the tests_passed pass-through.
/// </summary>
public sealed class TestWorkflowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tckit-workflow-" + Guid.NewGuid().ToString("N"));

    public TestWorkflowTests() => Directory.CreateDirectory(_dir);

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

    private sealed class FakePorts : IProjectWriter, IBuildRunner, ITestRunner
    {
        public List<string> Calls { get; } = [];
        public Result OpenResult { get; set; } = Result.Ok();
        public BuildResult BuildResult { get; set; } = new() { Success = true };
        public Result DeployResult { get; set; } = Result.Ok();
        public TestRunResult RunResult { get; set; } = new() { Success = true, TestsPassed = true };

        public Task<Result> OpenProjectAsync(string solutionPath, CancellationToken ct)
        {
            Calls.Add("open");
            return Task.FromResult(OpenResult);
        }

        public Task<BuildResult> BuildAsync(string? plcName, bool forceLog, CancellationToken ct)
        {
            Calls.Add("build");
            return Task.FromResult(BuildResult);
        }

        public Task<Result> DeployAsync(string targetAmsId, string? plcName, bool bootAutostart, CancellationToken ct)
        {
            Calls.Add(bootAutostart ? "deploy(autostart)" : "deploy");
            return Task.FromResult(DeployResult);
        }

        public Task<TestRunResult> RunTestsAsync(
            string targetAmsId, string? plcName, bool waitForResults, int timeoutSeconds, CancellationToken ct)
        {
            Calls.Add("tests");
            return Task.FromResult(RunResult);
        }

        public Task<TestResults> GetResultsAsync(
            string targetAmsId, string? plcName, string? xmlPath, CancellationToken ct)
            => throw new NotSupportedException();

        // The writer port's authoring surface is irrelevant to the workflow; nothing below runs.
#pragma warning disable IDE0060
        public Task<Result> CreateProjectAsync(string name, string path, CancellationToken ct) => Unused();
        public Task<Result> AddPlcProjectAsync(string solutionPath, string plcName, string projectType, CancellationToken ct) => Unused();
        public Task<Result> AddPouAsync(string name, PouType pouType, string code, string parentFolder, string? plcName, CancellationToken ct) => Unused();
        public Task<Result> AddFolderAsync(string name, string parentPath, string? plcName, CancellationToken ct) => Unused();
        public Task<Result> AddGvlAsync(string name, string code, string parentFolder, string? plcName, CancellationToken ct) => Unused();
        public Task<Result> AddDutAsync(string name, string code, DutKind dutKind, string parentFolder, string? plcName, CancellationToken ct) => Unused();
        public Task<Result> AddMethodAsync(string pouName, string methodName, string code, string? plcName, CancellationToken ct) => Unused();
        public Task<Result> AddPropertyAsync(string pouName, string propertyName, string returnType, string? getterCode, string? setterCode, string? plcName, CancellationToken ct) => Unused();
        public Task<Result> AddVariableAsync(string pouName, string scope, string declaration, string? itemName, string? plcName, CancellationToken ct) => Unused();
        public Task<Result> UpdatePouDeclarationAsync(string pouName, string code, string? plcName, CancellationToken ct) => Unused();
        public Task<Result> UpdatePouImplementationAsync(string pouName, string code, string? plcName, CancellationToken ct) => Unused();
        public Task<Result> UpdateMethodBodyAsync(string pouName, string methodName, string code, string? plcName, CancellationToken ct) => Unused();
        public Task<Result> UpdatePouDeclarationPatchAsync(string pouName, string oldString, string newString, string? plcName, CancellationToken ct) => Unused();
        public Task<Result> UpdatePouImplementationPatchAsync(string pouName, string oldString, string newString, string? plcName, CancellationToken ct) => Unused();
        public Task<Result> UpdateMethodBodyPatchAsync(string pouName, string methodName, string oldString, string newString, string? plcName, CancellationToken ct) => Unused();
        public Task<Result> DeletePouAsync(string name, string? plcName, CancellationToken ct) => Unused();
        public Task<Result> DeleteMethodAsync(string pouName, string methodName, string? plcName, CancellationToken ct) => Unused();
        public Task<Result> DeletePropertyAsync(string pouName, string propertyName, string? plcName, CancellationToken ct) => Unused();
        public Task<Result> DeleteGvlAsync(string name, string? plcName, CancellationToken ct) => Unused();
        public Task<Result> DeleteDutAsync(string name, string? plcName, CancellationToken ct) => Unused();
        public Task<Result> DeleteFolderAsync(string name, string parentPath, bool recursive, string? plcName, CancellationToken ct) => Unused();
        public Task<Result> DeleteVariableAsync(string pouName, string variableName, string? itemName, string? plcName, CancellationToken ct) => Unused();
        public Task<Result> AddLibraryReferenceAsync(string? plcName, string libraryName, string version, string distributor, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? parameters, CancellationToken ct) => Unused();
        public Task<Result> DeleteLibraryReferenceAsync(string? plcName, string libraryName, string version, string distributor, CancellationToken ct) => Unused();
        public Task<Result> AddLibraryPlaceholderAsync(string? plcName, string placeholderName, string defaultLibrary, string version, string distributor, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? parameters, CancellationToken ct) => Unused();
        public Task<Result> SetPlaceholderParametersAsync(string? plcName, string placeholderName, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> parameters, CancellationToken ct) => Unused();
        public Task<Result> DeletePlaceholderAsync(string? plcName, string placeholderName, CancellationToken ct) => Unused();
        public Task<Result> SavePlcAsLibraryAsync(string? plcName, string outputPath, bool install, string repository, bool overwrite, CancellationToken ct) => Unused();
#pragma warning restore IDE0060

        private static Task<Result> Unused() => throw new NotSupportedException();
    }

    private Task<TestWorkflowResult> Run(FakePorts ports, string? junit = null)
        => TestWorkflow.RunAsync(
            ports, ports, ports, @"C:\proj\App.sln", "Plc", "1.2.3.4.1.1", 60, junit, CancellationToken.None);

    [Fact]
    public async Task AllStagesPass_RunsInOrder()
    {
        var ports = new FakePorts();

        var result = await Run(ports);

        Assert.True(result.Success);
        Assert.True(result.TestsPassed);
        Assert.Null(result.FailedStage);
        Assert.Equal(["open", "build", "deploy(autostart)", "tests"], ports.Calls);
    }

    [Fact]
    public async Task BuildFailure_StopsBeforeDeploy()
    {
        var ports = new FakePorts { BuildResult = BuildResult.Fail("syntax error") };

        var result = await Run(ports);

        Assert.False(result.Success);
        Assert.Equal("build", result.FailedStage);
        Assert.Equal("syntax error", result.Error);
        Assert.Equal(["open", "build"], ports.Calls);
        Assert.Null(result.Tests);
    }

    [Fact]
    public async Task DeployFailure_StopsBeforeTests()
    {
        var ports = new FakePorts { DeployResult = Result.Fail("no route") };

        var result = await Run(ports);

        Assert.Equal("deploy", result.FailedStage);
        Assert.Equal(["open", "build", "deploy(autostart)"], ports.Calls);
    }

    [Fact]
    public async Task TestFailures_AreOutcomeNotInfrastructure()
    {
        var ports = new FakePorts
        {
            RunResult = new TestRunResult { Success = true, TestsPassed = false },
        };

        var result = await Run(ports);

        Assert.True(result.Success);
        Assert.False(result.TestsPassed);
        Assert.Null(result.FailedStage);
    }

    [Fact]
    public async Task JunitCopy_WhenPublished()
    {
        var xml = Path.Combine(_dir, "source.xml");
        File.WriteAllText(xml, "<testsuites/>");
        var junit = Path.Combine(_dir, "out", "results.xml");
        var ports = new FakePorts
        {
            RunResult = new TestRunResult
            {
                Success = true, TestsPassed = true, XmlPublished = true, XmlPath = xml,
            },
        };

        var result = await Run(ports, junit);

        Assert.Equal(Path.GetFullPath(junit), result.JunitPath);
        Assert.Equal("<testsuites/>", File.ReadAllText(junit));
    }

    [Fact]
    public async Task JunitSkipped_WhenNotPublished()
    {
        var junit = Path.Combine(_dir, "results.xml");
        var ports = new FakePorts
        {
            RunResult = new TestRunResult { Success = true, TestsPassed = false, XmlPublished = false },
        };

        var result = await Run(ports, junit);

        Assert.Null(result.JunitPath);
        Assert.False(File.Exists(junit));
    }

    [Fact]
    public async Task MissingArguments_Throw()
    {
        var ports = new FakePorts();

        await Assert.ThrowsAsync<ArgumentException>(() => TestWorkflow.RunAsync(
            ports, ports, ports, "", "Plc", "1.2.3.4.1.1", 60, null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => TestWorkflow.RunAsync(
            ports, ports, ports, @"C:\proj\App.sln", "Plc", "", 60, null, CancellationToken.None));
    }
}
