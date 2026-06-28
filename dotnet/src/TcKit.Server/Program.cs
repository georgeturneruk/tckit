using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TcKit.Adapters.Reader;
using TcKit.Core.Ports;

// TcKit MCP server host. stdio transport for the local case; SSE is wired in a
// later phase for the separate-machines requirement (ADR-0015). Tools are
// discovered by attribute from this assembly.
var builder = Host.CreateApplicationBuilder(args);

// On the stdio transport, stdout IS the JSON-RPC channel; any log written there
// corrupts the protocol stream. Route all logs to stderr.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<IProjectReader, XmlProjectReader>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
