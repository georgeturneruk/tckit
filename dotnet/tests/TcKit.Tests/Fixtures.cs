using System.Runtime.CompilerServices;

namespace TcKit.Tests;

/// <summary>Absolute paths to the in-repo TwinCAT fixtures, resolved from this source file's location.</summary>
internal static class Fixtures
{
    public static string SampleProject => Path.Combine(RepoRoot(), "tests", "fixtures", "sample_project");

    public static string MultiProject => Path.Combine(RepoRoot(), "tests", "fixtures", "multi_project_sln");

    public static string T3Solution => Path.Combine(
        RepoRoot(), "bench", "fixtures", "bug-hunting", "T3-tckit-utils", "T3TckitUtils.sln");

    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        // thisFile = <repo>/dotnet/tests/TcKit.Tests/Fixtures.cs
        var testsDir = Path.GetDirectoryName(thisFile)!;            // TcKit.Tests
        return Directory.GetParent(testsDir)!.Parent!.Parent!.FullName; // tests -> dotnet -> repo root
    }
}
