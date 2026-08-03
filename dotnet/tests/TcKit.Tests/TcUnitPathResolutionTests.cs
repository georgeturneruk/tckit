using TcKit.Ads;

namespace TcKit.Tests;

/// <summary>
/// TcUnit results-path resolution against fixture directories: env override, target-aware UmRT
/// registry match (including stale-kernel-file precedence), kernel candidates, and the freshest
/// UmRT heuristic fallback.
/// </summary>
public sealed class TcUnitPathResolutionTests : IDisposable
{
    private const string XmlFileName = "tcunit_xunit_testresults.xml";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tckit-paths-" + Guid.NewGuid().ToString("N"));
    private readonly string _kernelBoot;
    private readonly string _runtimesRoot;

    public TcUnitPathResolutionTests()
    {
        _kernelBoot = Path.Combine(_dir, "TwinCAT", "3.1", "Boot");
        _runtimesRoot = Path.Combine(_dir, "ProgramData", "Runtimes");
        Directory.CreateDirectory(_kernelBoot);
        Directory.CreateDirectory(_runtimesRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone.
        }
    }

    /// <summary>Create a UmRT install with a TcRegistry.xml mirroring the real 4026 shape.</summary>
    private string AddRuntime(string name, string netIdHex, string? bootDirValue = null)
    {
        var versionDir = Path.Combine(_runtimesRoot, name, "3.1");
        var bootDir = Path.Combine(versionDir, "Boot");
        Directory.CreateDirectory(bootDir);
        File.WriteAllText(
            Path.Combine(versionDir, "TcRegistry.xml"),
            $"""
            <?xml version="1.0"?>
            <TcRegistry>
              <Key Name="HKLM"><Key Name="Software"><Key Name="Beckhoff"><Key Name="TwinCAT3">
                <Key Name="System">
                  <Value Name="AmsNetId" Type="BIN">{netIdHex}</Value>
                  <Value Name="DefaultTransportAmsNetId" Type="BIN">535542010101</Value>
                </Key>
                <Key Name="3.1">
                  <Value Name="BootDir" Type="SZ">{bootDirValue ?? bootDir}</Value>
                </Key>
              </Key></Key></Key></Key>
            </TcRegistry>
            """);
        return bootDir;
    }

    [Fact]
    public void EnvOverride_Wins()
    {
        var (path, warning) = TcUnitResults.ResolveDefaultPath(
            "192.168.1.20.1.1", 851, envOverride: @"D:\custom\results.xml", _kernelBoot, _runtimesRoot);

        Assert.Equal(@"D:\custom\results.xml", path);
        Assert.Equal("", warning);
    }

    [Fact]
    public void TargetMatchesRuntimeRegistry_ReturnsItsBootPath_EvenWithoutFile()
    {
        // C0A801140101 = 192.168.1.20.1.1
        var bootDir = AddRuntime("UmRT_Default", "C0A801140101");

        var (path, warning) = TcUnitResults.ResolveDefaultPath(
            "192.168.1.20.1.1", 851, envOverride: null, _kernelBoot, _runtimesRoot);

        Assert.Equal(Path.Combine(bootDir, XmlFileName), path);
        Assert.Equal("", warning);
        Assert.False(File.Exists(path)); // resolution must not require the file (run-tests resolves first)
    }

    [Fact]
    public void TargetMatch_BeatsStaleKernelFile()
    {
        var kernelPortDir = Path.Combine(_kernelBoot, "Plc", "Port_851");
        Directory.CreateDirectory(kernelPortDir);
        File.WriteAllText(Path.Combine(kernelPortDir, XmlFileName), "<stale/>");
        var bootDir = AddRuntime("UmRT_Default", "C0A801140101");

        var (path, _) = TcUnitResults.ResolveDefaultPath(
            "192.168.1.20.1.1", 851, envOverride: null, _kernelBoot, _runtimesRoot);

        Assert.Equal(Path.Combine(bootDir, XmlFileName), path);
    }

    [Fact]
    public void TargetMatch_NormalisesDoubledSeparatorsInBootDir()
    {
        // The real TcRegistry.xml writes BootDir with a doubled separator ('...\3.1\\Boot\');
        // built with the platform separator so the collapse also verifies on the Linux CI.
        var versionDir = Path.Combine(_runtimesRoot, "UmRT_Default", "3.1");
        var sep = Path.DirectorySeparatorChar;
        var doubled = $"{versionDir}{sep}{sep}Boot{sep}";
        AddRuntime("UmRT_Default", "C0A801140101", bootDirValue: doubled);

        var (path, _) = TcUnitResults.ResolveDefaultPath(
            "192.168.1.20.1.1", 851, envOverride: null, _kernelBoot, _runtimesRoot);

        Assert.Equal(Path.Combine(versionDir, "Boot", XmlFileName), path);
    }

    [Fact]
    public void NoTargetMatch_ExistingKernelPortFile_Wins()
    {
        AddRuntime("UmRT_Default", "C0A801140101");
        var kernelPortDir = Path.Combine(_kernelBoot, "Plc", "Port_851");
        Directory.CreateDirectory(kernelPortDir);
        var kernelFile = Path.Combine(kernelPortDir, XmlFileName);
        File.WriteAllText(kernelFile, "<results/>");

        var (path, _) = TcUnitResults.ResolveDefaultPath(
            "192.168.0.9.1.1", 851, envOverride: null, _kernelBoot, _runtimesRoot);

        Assert.Equal(kernelFile, path);
    }

    [Fact]
    public void NoTargetMatch_ExistingKernelBootRootFile_Wins()
    {
        var kernelFile = Path.Combine(_kernelBoot, XmlFileName);
        File.WriteAllText(kernelFile, "<results/>");

        var (path, _) = TcUnitResults.ResolveDefaultPath(
            "192.168.0.9.1.1", 851, envOverride: null, _kernelBoot, _runtimesRoot);

        Assert.Equal(kernelFile, path);
    }

    [Fact]
    public void NoKernelFile_SingleUmRtCandidate_Wins()
    {
        var bootDir = AddRuntime("UmRT_Default", "C0A801140101");
        var umrtFile = Path.Combine(bootDir, XmlFileName);
        File.WriteAllText(umrtFile, "<results/>");

        var (path, warning) = TcUnitResults.ResolveDefaultPath(
            targetAmsId: null, 851, envOverride: null, _kernelBoot, _runtimesRoot);

        Assert.Equal(umrtFile, path);
        Assert.Equal("", warning);
    }

    [Fact]
    public void MultipleUmRtCandidates_FreshestWinsWithWarning()
    {
        var oldBoot = AddRuntime("UmRT_Old", "C0A801140101");
        var newBoot = AddRuntime("UmRT_New", "C0A801150101");
        var oldFile = Path.Combine(oldBoot, XmlFileName);
        var newFile = Path.Combine(newBoot, XmlFileName);
        File.WriteAllText(oldFile, "<results/>");
        File.WriteAllText(newFile, "<results/>");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(newFile, DateTime.UtcNow);

        var (path, warning) = TcUnitResults.ResolveDefaultPath(
            targetAmsId: null, 851, envOverride: null, _kernelBoot, _runtimesRoot);

        Assert.Equal(newFile, path);
        Assert.Contains("Multiple UmRT runtimes", warning);
        Assert.Contains(oldFile, warning);
    }

    [Fact]
    public void NothingFound_FallsBackToKernelPortPath()
    {
        var (path, warning) = TcUnitResults.ResolveDefaultPath(
            "192.168.0.9.1.1", 852, envOverride: null, _kernelBoot, _runtimesRoot);

        Assert.Equal(Path.Combine(_kernelBoot, "Plc", "Port_852", XmlFileName), path);
        Assert.Equal("", warning);
    }
}
