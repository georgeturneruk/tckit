using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TcKit.Adapters.Ads;
using TcKit.Adapters.Analysis;
using TcKit.Adapters.Automation;
using TcKit.Adapters.DocGen;
using TcKit.Adapters.Docs;
using TcKit.Adapters.Xml;
using TcKit.Core.Ports;
using TcKit.Core.Security;

// TcKit MCP server host. stdio transport for the local case; SSE is wired in a
// later phase for the separate-machines requirement (ADR-0015). Tools are
// discovered by attribute from this assembly.
var builder = Host.CreateApplicationBuilder(args);

// On the stdio transport, stdout IS the JSON-RPC channel; any log written there
// corrupts the protocol stream. Route all logs to stderr.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

// The safety gate is read by every mutating tool; a singleton so its hot-reload mtime cache is shared.
builder.Services.AddSingleton<IPermissionGate>(_ => new FilePermissionGate());

builder.Services.AddSingleton<IProjectReader, XmlProjectReader>();
builder.Services.AddSingleton<IProjectAnalyser, ProjectAnalyser>();

// Writer backend selection (ADR-0017): resolved once at startup, never per call. An attached
// XAE regenerates files from its stale in-memory tree on its next save, so interleaving the two
// backends within a session would silently revert on-disk edits.
builder.Services.AddSingleton<IProjectWriter>(_ => CreateProjectWriter());

// The COM-backed lanes only exist on Windows; a guarded factory turns the first use on another
// host into a clear tool error instead of a DI activation exception from the STA thread.
builder.Services.AddSingleton<IBuildRunner>(_ =>
{
    if (!OperatingSystem.IsWindows())
    {
        throw new PlatformNotSupportedException(
            "Build/Deploy needs Windows with XAE (COM Automation Interface); not available on this host.");
    }

    return new AutomationBuildRunner();
});
builder.Services.AddSingleton<IRuntimeControl, AdsRuntimeControl>();
builder.Services.AddSingleton<ITestRunner, TcUnitTestRunner>();
builder.Services.AddSingleton<ISymbolIo, AdsSymbolIo>();
builder.Services.AddSingleton<IHardwareInspector, TwinSharpHardwareInspector>();
builder.Services.AddSingleton<IHardwareScanner>(_ =>
{
    if (!OperatingSystem.IsWindows())
    {
        throw new PlatformNotSupportedException(
            "ScanHardware needs Windows with XAE (COM Automation Interface); not available on this host.");
    }

    return new AutomationHardwareScanner();
});
builder.Services.AddSingleton<IHardwareConfigurer>(_ =>
{
    if (!OperatingSystem.IsWindows())
    {
        throw new PlatformNotSupportedException(
            "I/O tree authoring needs Windows with XAE (COM Automation Interface); not available on this host.");
    }

    return new AutomationHardwareConfigurer();
});
builder.Services.AddSingleton<IDocsSearcher>(_ => new BeckhoffInfosysSearcher());
builder.Services.AddSingleton<IDocGenerator, DocGenerator>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

// TCKIT_WRITER = automation | xml. Default: automation where XAE can exist (Windows), the
// deterministic xml backend everywhere else.
static IProjectWriter CreateProjectWriter()
{
    var choice = Environment.GetEnvironmentVariable("TCKIT_WRITER")?.Trim().ToLowerInvariant();
    if (choice is not (null or "" or "automation" or "xml"))
    {
        throw new InvalidOperationException(
            $"Unknown TCKIT_WRITER value '{choice}'; use 'automation' or 'xml'.");
    }

    if (choice == "xml" || (string.IsNullOrEmpty(choice) && !OperatingSystem.IsWindows()))
    {
        return new XmlProjectWriter();
    }

    if (!OperatingSystem.IsWindows())
    {
        throw new InvalidOperationException(
            "TCKIT_WRITER=automation needs Windows with a running XAE; use TCKIT_WRITER=xml here.");
    }

    return new AutomationProjectWriter();
}
