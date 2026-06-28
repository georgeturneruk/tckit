namespace TcKit.Core.Models;

/// <summary>Lifecycle of a build/deploy operation (mirrors the Python BuildStatus).</summary>
public enum BuildStatus
{
    Idle,
    Building,
    Success,
    Error,
}

/// <summary>
/// One PLC Error List diagnostic. <see cref="Code"/> is the compiler code (e.g. "C0046") lifted off
/// the message; <see cref="Project"/> is the owning PLC project. Both default empty for the
/// build-output fallback that can't supply them.
/// </summary>
public sealed record BuildError(
    string File, int Line, string Message, string Severity = "error", string Code = "", string Project = "");

/// <summary>The outcome of a TwinCAT build: a success flag plus structured diagnostics by severity.</summary>
public sealed record BuildResult
{
    public required bool Success { get; init; }
    public IReadOnlyList<BuildError> Errors { get; init; } = [];
    public IReadOnlyList<BuildError> Warnings { get; init; } = [];
    public IReadOnlyList<BuildError> Infos { get; init; } = [];
    public double? DurationSeconds { get; init; }
    public IReadOnlyDictionary<string, object?> Details { get; init; } = new Dictionary<string, object?>();
    public string? Error { get; init; }

    public static BuildResult Fail(string error) => new()
    {
        Success = false,
        Errors = [new BuildError("", 0, error)],
        Error = error,
    };
}
