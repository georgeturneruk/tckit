namespace TcKit.Tests;

/// <summary>
/// Builds a throwaway single-PLC TwinCAT solution on disk for xml-writer tests: the file-system
/// analogue of <see cref="FakeProject"/>. Standard XAE shape: POUs / DUTs roots, a task-bound
/// MAIN, a placeholder and a library reference, and an opaque ProjectExtensions blob whose
/// byte-for-byte survival the tests assert.
/// </summary>
internal static class XmlScratch
{
    public const string PlcName = "Plc";

    /// <summary>Create the scratch solution under a fresh directory; returns the .sln path.</summary>
    public static string Create(string root)
    {
        var plcDir = Path.Combine(root, PlcName);
        Directory.CreateDirectory(Path.Combine(plcDir, "POUs"));
        Directory.CreateDirectory(Path.Combine(plcDir, "DUTs"));

        File.WriteAllText(Path.Combine(root, "Scratch.sln"), SlnText);
        File.WriteAllText(Path.Combine(plcDir, $"{PlcName}.plcproj"), PlcProjText);
        File.WriteAllText(Path.Combine(plcDir, "PlcTask.TcTTO"), TcttoText);
        File.WriteAllText(Path.Combine(plcDir, "POUs", "MAIN.TcPOU"), MainText);
        return Path.Combine(root, "Scratch.sln");
    }

    public static string PlcProjPath(string root) => Path.Combine(root, PlcName, $"{PlcName}.plcproj");

    public static string PouPath(string root, string name) => Path.Combine(root, PlcName, "POUs", name);

    private const string SlnText = """
        Microsoft Visual Studio Solution File, Format Version 12.00
        # TcKit xml-writer scratch solution
        """;

    /// <summary>The XmlArchive blob is nonsense on purpose: only byte survival matters.</summary>
    public const string XmlArchiveBlob = """
            <PlcProjectOptions>
              <XmlArchive>
                <Data>
                  <o xml:space="preserve" t="OptionKey">
                    <v n="Name">"&lt;ProjectRoot&gt;"</v>
                  </o>
                </Data>
                <TypeList>
                  <Type n="OptionKey">{54dd0eac-a6d8-46f2-8c27-2f43c7e49861}</Type>
                </TypeList>
              </XmlArchive>
            </PlcProjectOptions>
        """;

    private const string PlcProjText = $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup>
            <FileVersion>1.0.0.0</FileVersion>
            <SchemaVersion>2.0</SchemaVersion>
            <ProjectGuid>{d0000000-0000-0000-0000-000000000001}</ProjectGuid>
            <Name>Plc</Name>
            <ProgramVersion>3.1.4026.22</ProgramVersion>
            <Title>Plc</Title>
            <ProjectVersion>1.0.0.0</ProjectVersion>
          </PropertyGroup>
          <ItemGroup>
            <Compile Include="PlcTask.TcTTO">
              <SubType>Code</SubType>
            </Compile>
            <Compile Include="POUs\MAIN.TcPOU">
              <SubType>Code</SubType>
            </Compile>
          </ItemGroup>
          <ItemGroup>
            <Folder Include="DUTs" />
            <Folder Include="POUs" />
          </ItemGroup>
          <ItemGroup>
            <PlaceholderReference Include="Tc2_Standard">
              <DefaultResolution>Tc2_Standard, * (Beckhoff Automation GmbH)</DefaultResolution>
              <Namespace>Tc2_Standard</Namespace>
            </PlaceholderReference>
            <LibraryReference Include="Tc2_System,3.4.20.0,Beckhoff Automation GmbH">
              <Namespace>Tc2_System</Namespace>
            </LibraryReference>
          </ItemGroup>
          <ProjectExtensions>
        {{XmlArchiveBlob}}
          </ProjectExtensions>
        </Project>
        """;

    private const string TcttoText = """
        <?xml version="1.0" encoding="utf-8"?>
        <TcPlcObject Version="1.1.0.1">
          <Task Name="PlcTask" Id="{f0000000-0000-0000-0000-000000000002}">
            <!--CycleTime in micro seconds.-->
            <CycleTime>10000</CycleTime>
            <Priority>20</Priority>
            <PouCall>
              <Name>MAIN</Name>
            </PouCall>
          </Task>
        </TcPlcObject>
        """;

    private const string MainText = """
        <?xml version="1.0" encoding="utf-8"?>
        <TcPlcObject Version="1.1.0.1">
          <POU Name="MAIN" Id="{a0000000-0000-0000-0000-000000000003}" SpecialFunc="None">
            <Declaration><![CDATA[PROGRAM MAIN
        VAR
            nCounter : INT;
        END_VAR]]></Declaration>
            <Implementation>
              <ST><![CDATA[nCounter := nCounter + 1;]]></ST>
            </Implementation>
          </POU>
        </TcPlcObject>
        """;
}
