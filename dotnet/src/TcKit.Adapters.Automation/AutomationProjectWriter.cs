using TcKit.Core.Models;
using TcKit.Core.Ports;

namespace TcKit.Adapters.Automation;

/// <summary>
/// COM Automation Interface <see cref="IProjectWriter"/>: authors POUs/folders/GVLs/DUTs/methods/
/// properties in the solution open in the attached TcXaeShell. All COM runs on a single STA thread
/// (<see cref="StaExecutor"/>); each verb attaches fresh, mutates, then SaveAll (mirrors the bridge).
/// Late-bound via <see cref="DteSession"/>.
/// </summary>
public sealed class AutomationProjectWriter : IProjectWriter, IDisposable
{
    private readonly StaExecutor _sta = new();

    public Task<Result> OpenProjectAsync(string solutionPath, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () =>
        {
            dynamic dte = DteSession.Attach();
            DteSession.OpenSolution(dte, solutionPath);
            return Ok(("solution_path", solutionPath));
        });

    public Task<Result> AddPouAsync(
        string name, PouType pouType, string code, string parentFolder, string? plcName,
        CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () =>
        {
            var (dte, plc, sm) = Open(plcName);
            var parent = DteSession.ResolveFolderPath(DteSession.GetPousFolder(sm, plc), parentFolder);
            dynamic item = ComRetry.Invoke(() => parent.CreateChild(name, TcKind.ForPou(pouType), null, null));
            if (!string.IsNullOrEmpty(code))
            {
                DteSession.SetItemSourceFromCode(item, code);
            }

            DteSession.Save(dte);
            return Ok(("name", name), ("plc_name", plc), ("path", (string)item.PathName));
        });

    public Task<Result> AddFolderAsync(
        string name, string parentPath, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () =>
        {
            var (dte, plc, sm) = Open(plcName);
            var root = DteSession.GetPlcProjectNode(sm, plc);
            var parent = DteSession.ResolveFolderPath(root, string.IsNullOrEmpty(parentPath) ? "POUs" : parentPath);
            dynamic item = ComRetry.Invoke(() => parent.CreateChild(name, TcKind.Folder, null, null));
            DteSession.Save(dte);
            return Ok(("name", name), ("plc_name", plc), ("path", (string)item.PathName));
        });

    public Task<Result> AddGvlAsync(
        string name, string code, string parentFolder, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () =>
        {
            var (dte, plc, sm) = Open(plcName);
            var parent = DteSession.ResolveFolderPath(DteSession.GetPousFolder(sm, plc), parentFolder);
            dynamic item = ComRetry.Invoke(() => parent.CreateChild(name, TcKind.Gvl, null, null));
            if (!string.IsNullOrEmpty(code))
            {
                // GVLs are declaration-only; do not split or write an implementation.
                DteSession.SetItemSource(item, code, null);
            }

            DteSession.Save(dte);
            return Ok(("name", name), ("plc_name", plc), ("path", (string)item.PathName));
        });

    public Task<Result> AddDutAsync(
        string name, string code, DutKind dutKind, string parentFolder, string? plcName,
        CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () =>
        {
            var (dte, plc, sm) = Open(plcName);
            var parent = DteSession.ResolveFolderPath(DteSession.GetDutsFolder(sm, plc), parentFolder);
            dynamic item = ComRetry.Invoke(() => parent.CreateChild(name, TcKind.ForDut(dutKind), null, null));
            if (!string.IsNullOrEmpty(code))
            {
                DteSession.SetItemSource(item, code, null);
            }

            DteSession.Save(dte);
            return Ok(("name", name), ("plc_name", plc), ("path", (string)item.PathName));
        });

    public Task<Result> AddMethodAsync(
        string pouName, string methodName, string code, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () =>
        {
            var (dte, plc, sm) = Open(plcName);
            dynamic pou = LocatePou(sm, plc, pouName);
            var kind = DteSession.IsInterfacePou(pou) ? TcKind.InterfaceMethod : TcKind.Method;
            dynamic item = ComRetry.Invoke(() => pou.CreateChild(methodName, kind, null, null));
            if (!string.IsNullOrEmpty(code))
            {
                DteSession.SetItemSourceFromCode(item, code);
            }

            DteSession.Save(dte);
            return Ok(("pou_name", pouName), ("method_name", methodName), ("plc_name", plc));
        });

    public Task<Result> AddPropertyAsync(
        string pouName, string propertyName, string returnType,
        string? getterCode, string? setterCode, string? plcName, CancellationToken cancellationToken)
        => RunAsync(cancellationToken, () =>
        {
            if (string.IsNullOrEmpty(getterCode) && string.IsNullOrEmpty(setterCode))
            {
                return Result.Fail("At least one of getterCode or setterCode must be supplied.");
            }

            var (dte, plc, sm) = Open(plcName);
            dynamic pou = LocatePou(sm, plc, pouName);
            var isInterface = DteSession.IsInterfacePou(pou);

            var kindProperty = isInterface ? TcKind.InterfaceProperty : TcKind.Property;
            var kindGet = isInterface ? TcKind.InterfacePropertyGet : TcKind.PropertyGet;
            var kindSet = isInterface ? TcKind.InterfacePropertySet : TcKind.PropertySet;
            // FB property parent takes [language, type, access]; an interface property takes the type.
            object propertyVInfo = isInterface ? returnType : new[] { "ST", returnType, "PUBLIC" };

            dynamic property = ComRetry.Invoke(() => pou.CreateChild(propertyName, kindProperty, null, propertyVInfo));

            if (!string.IsNullOrEmpty(getterCode))
            {
                dynamic get = ComRetry.Invoke(() => property.CreateChild("", kindGet, null, null));
                if (!isInterface)
                {
                    DteSession.SetItemSourceFromCode(get, getterCode);
                }
            }

            if (!string.IsNullOrEmpty(setterCode))
            {
                dynamic set = ComRetry.Invoke(() => property.CreateChild("", kindSet, null, null));
                if (!isInterface)
                {
                    DteSession.SetItemSourceFromCode(set, setterCode);
                }
            }

            DteSession.Save(dte);
            return Ok(("pou_name", pouName), ("property_name", propertyName), ("plc_name", plc));
        });

    private static (dynamic Dte, string Plc, dynamic SysManager) Open(string? plcName)
    {
        dynamic dte = DteSession.Attach();
        DteSession.UseSolution(dte, "");
        var plc = DteSession.ResolvePlcName(dte, plcName);
        return (dte, plc, DteSession.GetSysManager(dte, plc));
    }

    private static dynamic LocatePou(dynamic sm, string plc, string pouName)
        => DteSession.FindChild(DteSession.GetPousFolder(sm, plc), pouName)
            ?? throw new InvalidOperationException($"POU '{pouName}' not found in PLC project '{plc}'.");

    private static Result Ok(params (string Key, object? Value)[] details)
        => Result.Ok(details.ToDictionary(d => d.Key, d => d.Value));

    private Task<Result> RunAsync(CancellationToken cancellationToken, Func<Result> work)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult(_sta.Run(work));
        }
#pragma warning disable CA1031 // The writer boundary funnels every failure into the Result error contract.
        catch (Exception ex)
        {
            return Task.FromResult(Result.Fail(ex.Message));
        }
#pragma warning restore CA1031
    }

    public void Dispose() => _sta.Dispose();
}
