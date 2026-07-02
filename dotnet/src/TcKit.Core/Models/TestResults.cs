namespace TcKit.Core.Models;

/// <summary>One failed TcUnit assertion (one per JUnit &lt;failure&gt; element).</summary>
public sealed record TestFailureDetail(string Message, string Expected = "", string Actual = "", int Line = 0);

/// <summary>A single TcUnit test case and its assertion failures (empty when it passed).</summary>
public sealed record TestCaseResult
{
    public required string Name { get; init; }
    public required bool Passed { get; init; }
    public int Asserts { get; init; }
    public IReadOnlyList<TestFailureDetail> Failures { get; init; } = [];
    public double? DurationSeconds { get; init; }
}

/// <summary>A TcUnit test suite and its cases.</summary>
public sealed record TestSuiteResult
{
    public required string Name { get; init; }
    public IReadOnlyList<TestCaseResult> Tests { get; init; } = [];
}

/// <summary>Run-wide counters. Totals always reflect the full run even when suites are failures-only.</summary>
public sealed record TestSummary
{
    public int Suites { get; init; }
    public int Tests { get; init; }
    public int Asserts { get; init; }
    public int Failures { get; init; }
    public int Errors { get; init; }
    public double DurationSeconds { get; init; }
}

/// <summary>A lean failed-test entry for the test-loop view: suite + test + first message.</summary>
public sealed record FlatFailure(string SuiteName, string TestName, string Message);

/// <summary>
/// Parsed TcUnit results (the get_test_results contract). <see cref="Suites"/> carries the full
/// per-test list (passes included); <see cref="Failures"/> is the flat failed-test list.
/// </summary>
public sealed record TestResults
{
    public required bool Success { get; init; }
    public IReadOnlyList<TestSuiteResult> Suites { get; init; } = [];
    public TestSummary Summary { get; init; } = new();
    public IReadOnlyList<FlatFailure> Failures { get; init; } = [];
    public string XmlPath { get; init; } = "";
    public string ResolveWarning { get; init; } = "";
    public string? Error { get; init; }
}

/// <summary>
/// The run_tests contract: the run outcome plus inlined results. When the xUnit XML was published
/// (<see cref="XmlPublished"/>) and inlining is on, <see cref="Suites"/> is failures-only and
/// <see cref="Failures"/> is the flat list; otherwise call get_test_results for the full detail.
/// </summary>
public sealed record TestRunResult
{
    public required bool Success { get; init; }
    public double DurationSeconds { get; init; }
    public TestSummary Summary { get; init; } = new();
    public string XmlPath { get; init; } = "";
    public bool XmlPublished { get; init; }
    public bool ResultsIncluded { get; init; }
    public IReadOnlyList<TestSuiteResult> Suites { get; init; } = [];
    public IReadOnlyList<FlatFailure> Failures { get; init; } = [];
    public string ResolveWarning { get; init; } = "";
    public string? Error { get; init; }
    public IReadOnlyDictionary<string, object?> Details { get; init; } = new Dictionary<string, object?>();
}
