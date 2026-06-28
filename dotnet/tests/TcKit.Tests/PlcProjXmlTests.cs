using System.Xml;
using TcKit.Adapters.Automation;

namespace TcKit.Tests;

/// <summary>
/// The file-only .plcproj helpers (the documented exception to never-edit-XML): placeholder
/// discovery, presence probe, and the parameter-override splice, plus the ProjectAuthor verb that
/// orchestrates close -> edit -> reopen around it.
/// </summary>
public sealed class PlcProjXmlTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tckit-plcproj-" + Guid.NewGuid().ToString("N"));

    public PlcProjXmlTests() => Directory.CreateDirectory(_dir);

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

    private const string PlcProjBody =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <Project ToolsVersion="4.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <ItemGroup>
            <PlaceholderReference Include="TcUnit">
              <DefaultResolution>TcUnit, * (www.tcunit.org)</DefaultResolution>
              <Namespace>TcUnit</Namespace>
            </PlaceholderReference>
          </ItemGroup>
        </Project>
        """;

    private string WritePlcProj(string plcName = "Plc")
    {
        var path = Path.Combine(_dir, $"{plcName}.plcproj");
        File.WriteAllText(path, PlcProjBody);
        return path;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Params(string value)
        => new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["GVL_Param_TcUnit"] = new Dictionary<string, string> { ["xUnitEnablePublish"] = value },
        };

    [Fact]
    public void Find_ResolvesSingleMatch()
    {
        var path = WritePlcProj();
        Assert.Equal(path, PlcProjXml.Find(_dir, "Plc"));
    }

    [Fact]
    public void Find_NoMatch_Throws()
        => Assert.Throws<InvalidOperationException>(() => PlcProjXml.Find(_dir, "Missing"));

    [Fact]
    public void HasPlaceholder_TrueOnlyForDeclared()
    {
        var path = WritePlcProj();
        Assert.True(PlcProjXml.HasPlaceholder(path, "TcUnit"));
        Assert.False(PlcProjXml.HasPlaceholder(path, "Tc3_Module"));
    }

    [Fact]
    public void SetPlaceholderParameters_WritesUppercasedEmptyNamespaceBlock()
    {
        var path = WritePlcProj();

        PlcProjXml.SetPlaceholderParameters(path, "TcUnit", Params("TRUE"));

        var param = LoadParameter(path);
        Assert.Equal("GVL_PARAM_TCUNIT", param.GetAttribute("ListName"));
        Assert.Equal("", param.NamespaceURI); // <Parameter> resets to the empty namespace
        Assert.Equal("XUNITENABLEPUBLISH", param.SelectSingleNode("Key")!.InnerText);
        Assert.Equal("TRUE", param.SelectSingleNode("Value")!.InnerText);
    }

    [Fact]
    public void SetPlaceholderParameters_IsIdempotent_ReplacesMatchingKey()
    {
        var path = WritePlcProj();

        PlcProjXml.SetPlaceholderParameters(path, "TcUnit", Params("FALSE"));
        PlcProjXml.SetPlaceholderParameters(path, "TcUnit", Params("TRUE"));

        var doc = new XmlDocument();
        doc.Load(path);
        var matches = doc.GetElementsByTagName("Parameter");
        Assert.Equal(1, matches.Count);
        Assert.Equal("TRUE", matches[0]!.SelectSingleNode("Value")!.InnerText);
    }

    [Fact]
    public void SetPlaceholderParameters_UnknownPlaceholder_Throws()
    {
        var path = WritePlcProj();
        Assert.Throws<InvalidOperationException>(
            () => PlcProjXml.SetPlaceholderParameters(path, "Absent", Params("TRUE")));
    }

    [Fact]
    public void ProjectAuthor_SetPlaceholderParameters_ClosesEditsReopens()
    {
        var path = WritePlcProj();
        var (session, _, _, _) = FakeProject.BuildWithReferences("Plc");
        session.SolutionPath = Path.Combine(_dir, "Solution.sln");

        var result = ProjectAuthor.SetPlaceholderParameters(session, null, "TcUnit", Params("TRUE"));

        Assert.True(result.Success);
        Assert.True(session.Closed); // closed before the on-disk edit
        Assert.Equal("TRUE", LoadParameter(path).SelectSingleNode("Value")!.InnerText);
    }

    private static XmlElement LoadParameter(string path)
    {
        var doc = new XmlDocument();
        doc.Load(path);
        return (XmlElement)doc.GetElementsByTagName("Parameter")[0]!;
    }
}
