using TcKit.Core.Analysis;
using TcKit.Core.Models;

namespace TcKit.Adapters.Analysis;

/// <summary>
/// Flattens what the reader returns into the <see cref="NamedSymbol"/> list the rules run over.
/// Kept free of IO so the mapping can be tested against hand-built source.
/// </summary>
public static class SymbolCollector
{
    /// <summary>
    /// Index every type the project declares so a variable's type expression can be classified.
    /// Aliases resolve to <see cref="TypeClass.Unknown"/>: the structure listing carries the DUT
    /// kind but not the aliased base type, and guessing would cost precision.
    /// </summary>
    public static IReadOnlyDictionary<string, TypeClass> BuildTypeIndex(ProjectStructure structure)
    {
        ArgumentNullException.ThrowIfNull(structure);

        var index = new Dictionary<string, TypeClass>(StringComparer.OrdinalIgnoreCase);
        foreach (var plc in structure.Plcs.Values)
        {
            foreach (var pou in plc.Pous)
            {
                if (pou.PouType is PouType.FunctionBlock)
                {
                    index[pou.Name] = TypeClass.FbInstance;
                }
                else if (pou.PouType is PouType.Interface)
                {
                    index[pou.Name] = TypeClass.Interface;
                }
            }

            foreach (var dut in plc.Duts)
            {
                index[dut.Name] = dut.DutKind switch
                {
                    DutKind.Struct or DutKind.Union => TypeClass.Struct,
                    DutKind.Enum => TypeClass.Enum,
                    _ => TypeClass.Unknown,
                };
            }
        }

        return index;
    }

    /// <summary>Collect the POU itself, its members, and every variable declared in either.</summary>
    public static List<NamedSymbol> FromPou(PouSource pou, string plcName, TypeClassifier classifier)
    {
        ArgumentNullException.ThrowIfNull(pou);
        ArgumentNullException.ThrowIfNull(classifier);

        var symbols = new List<NamedSymbol>();
        var declaration = DeclarationParser.Parse(pou.Declaration);

        symbols.Add(new NamedSymbol
        {
            Name = pou.PouName,
            Kind = KindOf(pou.PouType),
            PlcName = plcName,
            ObjectName = pou.PouName,
            Line = 1,
            Accessibility = declaration.Header.Accessibility,
        });

        // A FUNCTION has no instance surface: its VAR_INPUT is a parameter list and its VAR is
        // locals, exactly like a method, so its variables are collected at member scope.
        var ownScope = pou.PouType is PouType.Function ? SymbolScope.Member : SymbolScope.Object;
        symbols.AddRange(Variables(declaration, pou.PouName, "", plcName, ownScope, classifier));

        foreach (var member in pou.Members)
        {
            var memberDeclaration = DeclarationParser.Parse(member.Declaration);

            // Property accessors are not separately named by the user ("Status.Get"), so only the
            // property header earns a name finding; the accessors still contribute their locals.
            if (member.Kind is PouMemberKind.Method or PouMemberKind.Action or PouMemberKind.Property)
            {
                symbols.Add(new NamedSymbol
                {
                    Name = member.Name,
                    Kind = member.Kind switch
                    {
                        PouMemberKind.Method => SymbolKind.Method,
                        PouMemberKind.Action => SymbolKind.Action,
                        _ => SymbolKind.Property,
                    },
                    PlcName = plcName,
                    ObjectName = pou.PouName,
                    ItemName = member.Name,
                    Line = 1,
                    Accessibility = memberDeclaration.Header.Accessibility,
                });
            }

            symbols.AddRange(Variables(
                memberDeclaration, pou.PouName, member.Name, plcName, SymbolScope.Member, classifier));
        }

        return symbols;
    }

    /// <summary>Collect a GVL and the globals it declares.</summary>
    public static List<NamedSymbol> FromGvl(Gvl gvl, string plcName, TypeClassifier classifier)
    {
        ArgumentNullException.ThrowIfNull(gvl);
        ArgumentNullException.ThrowIfNull(classifier);

        var symbols = new List<NamedSymbol>
        {
            new()
            {
                Name = gvl.Name,
                Kind = SymbolKind.Gvl,
                PlcName = plcName,
                ObjectName = gvl.Name,
                Line = 1,
            },
        };

        symbols.AddRange(Variables(
            DeclarationParser.Parse(gvl.Declaration), gvl.Name, "", plcName, SymbolScope.Object, classifier));
        return symbols;
    }

    /// <summary>Collect a DUT and its members (struct fields or enumeration constants).</summary>
    public static List<NamedSymbol> FromDut(Dut dut, string plcName, TypeClassifier classifier)
    {
        ArgumentNullException.ThrowIfNull(dut);
        ArgumentNullException.ThrowIfNull(classifier);

        var kind = dut.DutKind switch
        {
            DutKind.Struct => SymbolKind.Struct,
            DutKind.Union => SymbolKind.Union,
            DutKind.Enum => SymbolKind.Enum,
            _ => SymbolKind.Alias,
        };

        var symbols = new List<NamedSymbol>
        {
            new()
            {
                Name = dut.Name,
                Kind = kind,
                PlcName = plcName,
                ObjectName = dut.Name,
                Line = 1,
            },
        };

        var memberKind = dut.DutKind is DutKind.Enum ? SymbolKind.EnumMember : SymbolKind.StructMember;
        foreach (var member in DeclarationParser.ParseType(dut.Declaration).Members)
        {
            symbols.Add(new NamedSymbol
            {
                Name = member.Name,
                Kind = memberKind,
                PlcName = plcName,
                ObjectName = dut.Name,
                Line = member.Line,
                TypeClass = classifier.Classify(member.TypeExpression),
                TypeExpression = member.TypeExpression,
            });
        }

        return symbols;
    }

    private static IEnumerable<NamedSymbol> Variables(
        StDeclaration declaration,
        string objectName,
        string itemName,
        string plcName,
        SymbolScope scope,
        TypeClassifier classifier)
        => declaration.Variables.Select(variable => new NamedSymbol
        {
            Name = variable.Name,
            Kind = SymbolKind.Variable,
            PlcName = plcName,
            ObjectName = objectName,
            ItemName = itemName,
            Line = variable.Line,
            Section = variable.Section,
            Scope = scope,
            Qualifiers = variable.Qualifiers,
            TypeClass = classifier.Classify(variable.TypeExpression),
            TypeExpression = variable.TypeExpression,
        });

    private static SymbolKind KindOf(PouType type) => type switch
    {
        PouType.Function => SymbolKind.Function,
        PouType.Program => SymbolKind.Program,
        PouType.Interface => SymbolKind.Interface,
        _ => SymbolKind.FunctionBlock,
    };
}
