using System.ComponentModel;
using ModelContextProtocol.Server;

namespace TcKit.Server.Tools;

/// <summary>Smoke-test tools that prove the MCP host is reachable without touching TwinCAT.</summary>
[McpServerToolType]
public static class DiagnosticsTool
{
    /// <summary>Returns "ok"; confirms the server is alive over the transport.</summary>
    // Explicit PascalCase name: the SDK would otherwise camelCase the method name to "ping".
    [McpServerTool(Name = "Ping")]
    [Description("Health check: returns ok if the TcKit MCP server is alive.")]
    public static string Ping() => "ok";
}
