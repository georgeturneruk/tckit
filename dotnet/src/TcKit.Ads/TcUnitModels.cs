namespace TcKit.Ads;

/// <summary>One failed TcUnit assertion (one per JUnit &lt;failure&gt; element).</summary>
public sealed record TcUnitFailure(string Message, string Expected = "", string Actual = "", int Line = 0);

/// <summary>A single TcUnit test case and its assertion failures (empty when it passed).</summary>
public sealed record TcUnitCase
{
    public required string Name { get; init; }
    public required bool Passed { get; init; }
    public int Asserts { get; init; }
    public IReadOnlyList<TcUnitFailure> Failures { get; init; } = [];
    public double? DurationSeconds { get; init; }
}

/// <summary>A TcUnit test suite and its cases.</summary>
public sealed record TcUnitSuite
{
    public required string Name { get; init; }
    public IReadOnlyList<TcUnitCase> Tests { get; init; } = [];
}

/// <summary>Run-wide counters. Totals always reflect the full run even when suites are failures-only.</summary>
public sealed record TcUnitSummary
{
    public int Suites { get; init; }
    public int Tests { get; init; }
    public int Asserts { get; init; }
    public int Failures { get; init; }
    public int Errors { get; init; }
    public double DurationSeconds { get; init; }
}

/// <summary>A lean failed-test entry: suite + test + first message.</summary>
public sealed record TcUnitFlatFailure(string SuiteName, string TestName, string Message);

/// <summary>
/// A parsed TcUnit results file. <see cref="Suites"/> carries the per-test tree (narrowed to
/// failures-only when requested); <see cref="Failures"/> is the flat failed-test list;
/// <see cref="Summary"/> always reflects the full run.
/// </summary>
public sealed record TcUnitParsed
{
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<TcUnitSuite> Suites { get; init; } = [];
    public TcUnitSummary Summary { get; init; } = new();
    public IReadOnlyList<TcUnitFlatFailure> Failures { get; init; } = [];
    public string XmlPath { get; init; } = "";
}
