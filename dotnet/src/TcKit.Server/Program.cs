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

// Structural writes go through the Automation Interface only (ADR-0019 retired the xml
// backend); like the other COM-backed lanes, a guarded factory turns first use on a
// non-Windows host into a clear tool error.
builder.Services.AddSingleton<IProjectWriter>(_ =>
{
    if (!OperatingSystem.IsWindows())
    {
        throw new PlatformNotSupportedException(
            "Structural writes need Windows with XAE (COM Automation Interface); not available on this host.");
    }

    return new AutomationProjectWriter();
});

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
