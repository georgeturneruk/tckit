using TcKit.Core.Authoring;

namespace TcKit.Tests;

/// <summary>Tests the file-side .TcTTO scan that backs the delete_pou PROGRAM task-binding refusal.</summary>
public class TaskBindingTests
{
    private static string T3Dir => Path.GetDirectoryName(Fixtures.T3Solution)!;

    [Fact]
    public void Find_ProgramBoundToTask_ReturnsTheTask()
    {
        var binding = TaskBinding.Find(T3Dir, "MAIN");

        Assert.NotNull(binding);
        Assert.Equal("PlcTask", binding!.Value.Task);
    }

    [Fact]
    public void Find_UnboundPou_ReturnsNull()
        => Assert.Null(TaskBinding.Find(T3Dir, "FB_Pid"));

    [Fact]
    public void Find_MissingDirectory_ReturnsNull()
        => Assert.Null(TaskBinding.Find("", "MAIN"));
}
