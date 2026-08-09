using System.Text;
using TcKit.Adapters.Xml;
using TcKit.Core.Models;

namespace TcKit.Tests;

/// <summary>
/// Byte-exact emission checks for the xml writer backend with a pinned GUID source: new files
/// must match XAE's canonical shape (UTF-8 BOM, CRLF, two-space indent, flush CDATA), and edits
/// must reproduce the file's existing BOM/EOL style. GuidSource is static state, so every test
/// that pins it lives in this class and restores it on dispose.
/// </summary>
[Collection("xml-writer")] // GuidSource is static; serialise every class that touches it
public sealed class XmlWriterEmissionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "tckit-xmlemit-" + Guid.NewGuid().ToString("N"));

    private readonly XmlProjectWriter _writer = new();
    private readonly string _sln;
    private int _guidCounter;

    public XmlWriterEmissionTests()
    {
        Directory.CreateDirectory(_root);
        _sln = XmlScratch.Create(_root);
        GuidSource.Next = () => new Guid($"{++_guidCounter:d8}-1111-1111-1111-111111111111");
    }

    public void Dispose()
    {
        GuidSource.Next = Guid.NewGuid;
        _writer.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private async Task OpenAsync()
        => Assert.True((await _writer.OpenProjectAsync(_sln, CancellationToken.None)).Success);

    [Fact]
    public async Task AddPou_EmitsCanonicalXaeShape()
    {
        await OpenAsync();

        var result = await _writer.AddPouAsync(
            "FB_X", PouType.FunctionBlock,
            "FUNCTION_BLOCK FB_X\nVAR\n    n : INT;\nEND_VAR\nn := n + 1;",
            "", null, CancellationToken.None);
        Assert.True(result.Success, result.Error);

        var bytes = File.ReadAllBytes(XmlScratch.PouPath(_root, "FB_X.TcPOU"));
        Assert.Equal(0xEF, bytes[0]); // UTF-8 BOM, as XAE writes
        var text = Encoding.UTF8.GetString(bytes[3..]);
        var expected = string.Join("\r\n",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>",
            "<TcPlcObject Version=\"1.1.0.1\">",
            "  <POU Name=\"FB_X\" Id=\"{00000001-1111-1111-1111-111111111111}\" SpecialFunc=\"None\">",
            "    <Declaration><![CDATA[FUNCTION_BLOCK FB_X",
            "VAR",
            "    n : INT;",
            "END_VAR]]></Declaration>",
            "    <Implementation>",
            "      <ST><![CDATA[n := n + 1;]]></ST>",
            "    </Implementation>",
            "  </POU>",
            "</TcPlcObject>");
        Assert.Equal(expected, text);
    }

    [Fact]
    public async Task AddMethod_NestsAtMemberDepth()
    {
        await OpenAsync();
        Assert.True((await _writer.AddPouAsync(
            "FB_X", PouType.FunctionBlock, "FUNCTION_BLOCK FB_X", "", null, CancellationToken.None)).Success);

        var result = await _writer.AddMethodAsync(
            "FB_X", "Execute", "METHOD Execute : BOOL\nExecute := TRUE;", null, CancellationToken.None);
        Assert.True(result.Success, result.Error);

        var text = Encoding.UTF8.GetString(File.ReadAllBytes(XmlScratch.PouPath(_root, "FB_X.TcPOU"))[3..]);
        var expected = string.Join("\r\n",
            "    <Method Name=\"Execute\" Id=\"{00000002-1111-1111-1111-111111111111}\">",
            "      <Declaration><![CDATA[METHOD Execute : BOOL]]></Declaration>",
            "      <Implementation>",
            "        <ST><![CDATA[Execute := TRUE;]]></ST>",
            "      </Implementation>",
            "    </Method>",
            "  </POU>");
        Assert.Contains(expected, text);
    }

    [Fact]
    public async Task Edit_PreservesLfNoBomStyle()
    {
        await OpenAsync();
        // The scratch MAIN.TcPOU is written LF without BOM (this repo's fixture style).
        var original = File.ReadAllBytes(XmlScratch.PouPath(_root, "MAIN.TcPOU"));
        Assert.NotEqual(0xEF, original[0]);
        Assert.DoesNotContain((byte)'\r', original);

        var result = await _writer.UpdatePouImplementationAsync("MAIN", "nCounter := 7;", null, CancellationToken.None);
        Assert.True(result.Success, result.Error);

        var edited = File.ReadAllBytes(XmlScratch.PouPath(_root, "MAIN.TcPOU"));
        Assert.NotEqual(0xEF, edited[0]);
        Assert.DoesNotContain((byte)'\r', edited);
        Assert.Contains("nCounter := 7;", Encoding.UTF8.GetString(edited));
    }

    [Fact]
    public async Task Edit_PreservesUntouchedRegionsByteForByte()
    {
        await OpenAsync();
        var before = Encoding.UTF8.GetString(File.ReadAllBytes(XmlScratch.PouPath(_root, "MAIN.TcPOU")));

        var result = await _writer.UpdatePouImplementationAsync("MAIN", "nCounter := 7;", null, CancellationToken.None);
        Assert.True(result.Success, result.Error);

        var after = Encoding.UTF8.GetString(File.ReadAllBytes(XmlScratch.PouPath(_root, "MAIN.TcPOU")));
        Assert.Equal(
            before.Replace("nCounter := nCounter + 1;", "nCounter := 7;", StringComparison.Ordinal),
            after);
    }
}
