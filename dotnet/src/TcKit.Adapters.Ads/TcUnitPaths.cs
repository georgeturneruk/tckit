namespace TcKit.Adapters.Ads;

/// <summary>
/// Resolve the path TcUnit's xUnit publisher writes results to. Mirrors
/// GVL_Param_TcUnit.xUnitFilePath (default '%TC_BOOTPRJPATH%tcunit_xunit_testresults.xml'), whose
/// expansion is runtime-kind dependent. Resolution order: TCKIT_TCUNIT_XML_PATH env override ->
/// kernel-RT path if present -> freshest UmRT candidate -> kernel-RT path string as a stable
/// fallback. Port of Get-TcUnitDefaultXmlPath in bridge/harness/_TcUnit.psm1.
/// </summary>
internal static class TcUnitPaths
{
    private const string XmlFileName = "tcunit_xunit_testresults.xml";

    public const int DefaultPlcPort = 851;

    public static (string Path, string Warning) ResolveDefault(int port = DefaultPlcPort)
    {
        var envOverride = Environment.GetEnvironmentVariable("TCKIT_TCUNIT_XML_PATH");
        if (!string.IsNullOrEmpty(envOverride))
        {
            return (envOverride, "");
        }

        var kernelPath = $@"C:\TwinCAT\3.1\Boot\Plc\Port_{port}\{XmlFileName}";
        if (File.Exists(kernelPath))
        {
            return (kernelPath, "");
        }

        var programData = Environment.GetEnvironmentVariable("ProgramData");
        if (!string.IsNullOrEmpty(programData))
        {
            var runtimesRoot = Path.Combine(programData, "Beckhoff", "TwinCAT", "3.1", "Runtimes");
            if (Directory.Exists(runtimesRoot))
            {
                var marker = $"{Path.DirectorySeparatorChar}3.1{Path.DirectorySeparatorChar}Boot{Path.DirectorySeparatorChar}";
                var candidates = Directory
                    .EnumerateFiles(runtimesRoot, XmlFileName, SearchOption.AllDirectories)
                    .Where(p => p.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ToList();

                if (candidates.Count == 1)
                {
                    return (candidates[0], "");
                }

                if (candidates.Count > 1)
                {
                    var others = string.Join("; ", candidates.Skip(1));
                    return (candidates[0],
                        "Multiple UmRT runtimes published TcUnit XML; using freshest. "
                        + $"Set TCKIT_TCUNIT_XML_PATH to pin. Alternatives: {others}");
                }
            }
        }

        return (kernelPath, "");
    }
}
