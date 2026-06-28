using TcKit.Core.Models;

namespace TcKit.Adapters.Automation;

/// <summary>
/// TwinCAT Automation Interface tree-item kind constants (the integer passed as the second
/// argument to <c>ITcSmTreeItem.CreateChild</c>), plus the maps from the domain enums.
/// Mirrors the bridge harness's <c>$script:TcKind</c> table.
/// </summary>
internal static class TcKind
{
    public const int Folder = 601;
    public const int Program = 602;
    public const int Function = 603;
    public const int FunctionBlock = 604;
    public const int Enum = 605;
    public const int Struct = 606;
    public const int Union = 607;
    public const int Action = 608;
    public const int Method = 609;
    public const int InterfaceMethod = 610;
    public const int Property = 611;
    public const int InterfaceProperty = 612;
    public const int PropertyGet = 613;
    public const int PropertySet = 614;
    public const int Gvl = 615;
    public const int Interface = 618;
    public const int Alias = 623;
    public const int InterfacePropertyGet = 654;
    public const int InterfacePropertySet = 655;

    public static int ForPou(PouType pouType) => pouType switch
    {
        PouType.Program => Program,
        PouType.Function => Function,
        PouType.FunctionBlock => FunctionBlock,
        PouType.Interface => Interface,
        _ => throw new ArgumentOutOfRangeException(nameof(pouType), pouType, "Unknown POU type."),
    };

    public static int ForDut(DutKind dutKind) => dutKind switch
    {
        DutKind.Struct => Struct,
        DutKind.Enum => Enum,
        DutKind.Union => Union,
        DutKind.Alias => throw new NotSupportedException("Alias DUT creation is not supported."),
        _ => throw new ArgumentOutOfRangeException(nameof(dutKind), dutKind, "Unknown DUT kind."),
    };
}
