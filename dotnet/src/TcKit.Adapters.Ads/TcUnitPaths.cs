using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace TcKit.Adapters.Ads;

/// <summary>
/// Resolve the path TcUnit's xUnit publisher writes results to. Mirrors
/// GVL_Param_TcUnit.xUnitFilePath (default '%TC_BOOTPRJPATH%tcunit_xunit_testresults.xml'), whose
/// expansion is runtime-kind dependent: on a user-mode runtime it is the Boot root of that
/// runtime's install; on a kernel RT it is under C:\TwinCAT\3.1\Boot. Resolution order:
/// TCKIT_TCUNIT_XML_PATH env override -> the boot folder of the UmRT whose TcRegistry.xml declares
/// the target's AMS Net ID (existence not required: run_tests resolves before the file is written)
/// -> existing kernel-RT file (Port_&lt;port&gt; subfolder, then Boot root) -> freshest existing UmRT
/// candidate -> kernel-RT Port_&lt;port&gt; path string as a stable fallback.
/// </summary>
internal static class TcUnitPaths
{
    private const string XmlFileName = "tcunit_xunit_testresults.xml";

    public const int DefaultPlcPort = 851;

    public static (string Path, string Warning) ResolveDefault(string? targetAmsId = null, int port = DefaultPlcPort)
        => ResolveDefault(
            targetAmsId,
            port,
            Environment.GetEnvironmentVariable("TCKIT_TCUNIT_XML_PATH"),
            @"C:\TwinCAT\3.1\Boot",
            RuntimesRoot());

    internal static (string Path, string Warning) ResolveDefault(
        string? targetAmsId, int port, string? envOverride, string kernelBootDir, string? runtimesRoot)
    {
        if (!string.IsNullOrEmpty(envOverride))
        {
            return (envOverride, "");
        }

        // Target-aware resolution: the UmRT that owns the target's AMS Net ID owns the result path,
        // regardless of what stale files exist elsewhere.
        if (!string.IsNullOrEmpty(targetAmsId) && runtimesRoot is not null)
        {
            var bootDir = FindRuntimeBootDir(runtimesRoot, targetAmsId);
            if (bootDir is not null)
            {
                return (Path.Combine(bootDir, XmlFileName), "");
            }
        }

        // Kernel RT: the Port_<port> subfolder is the historic default; the Boot root is where
        // %TC_BOOTPRJPATH% lands on user-mode runtimes and possibly on kernel targets too, so an
        // existing file in either counts.
        var kernelPortPath = Path.Combine(kernelBootDir, "Plc", $"Port_{port}", XmlFileName);
        if (File.Exists(kernelPortPath))
        {
            return (kernelPortPath, "");
        }

        var kernelRootPath = Path.Combine(kernelBootDir, XmlFileName);
        if (File.Exists(kernelRootPath))
        {
            return (kernelRootPath, "");
        }

        if (runtimesRoot is not null && Directory.Exists(runtimesRoot))
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

        return (kernelPortPath, "");
    }

    private static string? RuntimesRoot()
    {
        var programData = Environment.GetEnvironmentVariable("ProgramData");
        return string.IsNullOrEmpty(programData)
            ? null
            : Path.Combine(programData, "Beckhoff", "TwinCAT", "3.1", "Runtimes");
    }

    /// <summary>
    /// Find the boot folder of the user-mode runtime whose TcRegistry.xml declares
    /// <paramref name="targetAmsId"/>. The registry stores the Net ID as a 6-byte hex BIN value and
    /// the boot folder as the BootDir string (which may contain doubled separators).
    /// </summary>
    private static string? FindRuntimeBootDir(string runtimesRoot, string targetAmsId)
    {
        if (!Directory.Exists(runtimesRoot))
        {
            return null;
        }

        foreach (var runtimeDir in Directory.EnumerateDirectories(runtimesRoot))
        {
            var registryPath = Path.Combine(runtimeDir, "3.1", "TcRegistry.xml");
            if (!File.Exists(registryPath))
            {
                continue;
            }

            string? netIdHex;
            string? bootDir;
            try
            {
                var values = XDocument.Load(registryPath).Descendants("Value").ToList();
                netIdHex = values.FirstOrDefault(v => (string?)v.Attribute("Name") == "AmsNetId")?.Value;
                bootDir = values.FirstOrDefault(v => (string?)v.Attribute("Name") == "BootDir")?.Value;
            }
            catch (XmlException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (netIdHex is null || HexToDottedNetId(netIdHex) != targetAmsId)
            {
                continue;
            }

            return string.IsNullOrWhiteSpace(bootDir)
                ? Path.Combine(runtimeDir, "3.1", "Boot")
                : Path.GetFullPath(bootDir.Trim());
        }

        return null;
    }

    private static string? HexToDottedNetId(string hex)
    {
        hex = hex.Trim();
        if (hex.Length != 12)
        {
            return null;
        }

        var parts = new string[6];
        for (var i = 0; i < 6; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            parts[i] = value.ToString(CultureInfo.InvariantCulture);
        }

        return string.Join('.', parts);
    }
}
