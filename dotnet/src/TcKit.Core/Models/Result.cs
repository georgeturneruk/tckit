namespace TcKit.Core.Models;

/// <summary>The outcome of a structural write to a TwinCAT project (the writer contract).</summary>
public sealed record Result
{
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public IReadOnlyDictionary<string, object?> Details { get; init; } = new Dictionary<string, object?>();

    public static Result Ok(IReadOnlyDictionary<string, object?>? details = null)
        => new() { Success = true, Details = details ?? new Dictionary<string, object?>() };

    public static Result Fail(string error) => new() { Success = false, Error = error };
}
