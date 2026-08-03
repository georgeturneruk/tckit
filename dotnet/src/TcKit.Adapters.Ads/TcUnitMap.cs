using TcKit.Ads;
using TcKit.Core.Models;

namespace TcKit.Adapters.Ads;

/// <summary>
/// Map the TcKit.Ads TcUnit records onto the TcKit.Core result contracts. The library owns parsing
/// without a TcKit.Core dependency (ADR-0016); the adapter owns the MCP/CLI-facing shapes.
/// </summary>
internal static class TcUnitMap
{
    public static TestSummary ToModel(this TcUnitSummary summary) => new()
    {
        Suites = summary.Suites,
        Tests = summary.Tests,
        Asserts = summary.Asserts,
        Failures = summary.Failures,
        Errors = summary.Errors,
        DurationSeconds = summary.DurationSeconds,
    };

    public static TestSuiteResult ToModel(this TcUnitSuite suite) => new()
    {
        Name = suite.Name,
        Tests = suite.Tests.Select(ToModel).ToList(),
    };

    public static TestCaseResult ToModel(this TcUnitCase testCase) => new()
    {
        Name = testCase.Name,
        Passed = testCase.Passed,
        Asserts = testCase.Asserts,
        Failures = testCase.Failures.Select(ToModel).ToList(),
        DurationSeconds = testCase.DurationSeconds,
    };

    public static TestFailureDetail ToModel(this TcUnitFailure failure)
        => new(failure.Message, failure.Expected, failure.Actual, failure.Line);

    public static FlatFailure ToModel(this TcUnitFlatFailure failure)
        => new(failure.SuiteName, failure.TestName, failure.Message);
}
