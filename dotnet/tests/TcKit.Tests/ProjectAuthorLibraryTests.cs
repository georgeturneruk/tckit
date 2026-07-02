using TcKit.Adapters.Automation;

namespace TcKit.Tests;

/// <summary>
/// Library reference + placeholder verbs against the fake References node: AddLibrary/AddPlaceholder
/// append a reference child, RemoveReference drops it, and the "*" delete path resolves the
/// effective version off the child's ProduceXml.
/// </summary>
public class ProjectAuthorLibraryTests
{
    [Fact]
    public void AddLibraryReference_AppendsReferenceChild()
    {
        var (session, _, _, references) = FakeProject.BuildWithReferences("Plc");

        var result = ProjectAuthor.AddLibraryReference(session, null, "Tc2_Standard", "*", "Beckhoff Automation GmbH");

        Assert.True(result.Success);
        Assert.Equal("Plc", result.Details["consumer_plc"]);
        Assert.NotNull(references["Plc"].FindDirect("Tc2_Standard"));
        Assert.Equal(1, session.SaveCount);
    }

    [Fact]
    public void AddLibraryReference_Duplicate_Throws()
    {
        var (session, _, _, _) = FakeProject.BuildWithReferences("Plc");
        ProjectAuthor.AddLibraryReference(session, null, "Tc2_Standard", "*", "Beckhoff Automation GmbH");

        var ex = Assert.Throws<InvalidOperationException>(
            () => ProjectAuthor.AddLibraryReference(session, null, "Tc2_Standard", "*", "Beckhoff Automation GmbH"));
        Assert.Contains("already contained", ex.Message);
    }

    [Fact]
    public void DeleteLibraryReference_ExplicitVersion_Removes()
    {
        var (session, _, _, references) = FakeProject.BuildWithReferences("Plc");
        ProjectAuthor.AddLibraryReference(session, null, "MyLib", "1.2.3.4", "Tc3 Project");

        var result = ProjectAuthor.DeleteLibraryReference(session, null, "MyLib", "1.2.3.4", "Tc3 Project");

        Assert.True(result.Success);
        Assert.Null(references["Plc"].FindDirect("MyLib"));
    }

    [Fact]
    public void DeleteLibraryReference_WildcardVersion_ResolvesEffective()
    {
        var (session, _, _, references) = FakeProject.BuildWithReferences("Plc");
        // Added with "*"; the fake resolves EffectiveVersion to 1.0.0.0.
        ProjectAuthor.AddLibraryReference(session, null, "MyLib", "*", "Tc3 Project");

        var result = ProjectAuthor.DeleteLibraryReference(session, null, "MyLib", "*", "Tc3 Project");

        Assert.True(result.Success);
        Assert.Equal("1.0.0.0", result.Details["version"]);
        Assert.Null(references["Plc"].FindDirect("MyLib"));
    }

    [Fact]
    public void DeleteLibraryReference_WildcardNoMatch_Throws()
    {
        var (session, _, _, _) = FakeProject.BuildWithReferences("Plc");

        var ex = Assert.Throws<InvalidOperationException>(
            () => ProjectAuthor.DeleteLibraryReference(session, null, "Absent", "*", "Tc3 Project"));
        Assert.Contains("No library reference matching", ex.Message);
    }

    [Fact]
    public void AddLibraryPlaceholder_NoParameters_AppendsPlaceholder()
    {
        var (session, _, _, references) = FakeProject.BuildWithReferences("Plc");

        var result = ProjectAuthor.AddLibraryPlaceholder(
            session, null, "Tc3_Module", "Tc3_Module", "*", "Beckhoff Automation GmbH", parameters: null);

        Assert.True(result.Success);
        Assert.Equal(false, result.Details["already_present"]);
        var placeholder = references["Plc"].FindDirect("Tc3_Module");
        Assert.NotNull(placeholder);
        Assert.True(placeholder!.IsPlaceholder);
    }

    [Fact]
    public void DeletePlaceholder_RemovesPlaceholder()
    {
        var (session, _, _, references) = FakeProject.BuildWithReferences("Plc");
        ProjectAuthor.AddLibraryPlaceholder(session, null, "Tc3_Module", "Tc3_Module", "*", "", parameters: null);

        var result = ProjectAuthor.DeletePlaceholder(session, null, "Tc3_Module");

        Assert.True(result.Success);
        Assert.Null(references["Plc"].FindDirect("Tc3_Module"));
    }

    [Fact]
    public void AddLibraryReference_EmptyName_Throws()
    {
        var (session, _, _, _) = FakeProject.BuildWithReferences("Plc");

        Assert.Throws<ArgumentException>(
            () => ProjectAuthor.AddLibraryReference(session, null, "", "*", "Tc3 Project"));
    }
}
