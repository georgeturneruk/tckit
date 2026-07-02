# Hardware

Three lanes: live diagnostics and symbol I/O go over ADS (runtime reachable by AMS Net ID, no XAE); topology scanning and I/O authoring go through the Automation Interface (XAE open with the solution loaded).

## Diagnostics (ADS, read-only)

| Tool | Returns |
|---|---|
| `ListEtherCatMasters(targetAmsId)` | Every EtherCAT master on the target (usually one) |
| `GetEtherCatStatus(targetAmsId, masterNetId?)` | Master diagnostic flags plus the slave table: state machine, identity, link health, per-port CRC error counters |
| `GetIpcHardware(targetAmsId)` | TwinCAT version, CPU (temperature / usage / frequency), memory, fans, network adapters, UPS |
| `ListAxes(targetAmsId)` | All NC axes with live state: position, velocity, lag error, state name |
| `GetAxisState(targetAmsId, axisId)` | One axis, same fields |

## Live symbols (ADS)

| Tool | Purpose |
|---|---|
| `ReadSymbols(targetAmsId, paths)` | Read symbols by instance path (`MAIN.nCounter`, `GVL.bEnable`); unreadable paths map to null rather than failing |
| `WriteSymbols(targetAmsId, writesJson, confirmed)` | Write symbols; per-symbol failures are reported individually |
| `InvokeRpc(targetAmsId, symbolPath, methodName, paramsJson?, confirmed)` | Call a method decorated with `{attribute 'TcRpcEnable'}` on an FB instance |

`WriteSymbols` and `InvokeRpc` mutate live PLC state: they require `confirmed=true` and are execute-class under the [safety stance](../../getting-started/permissions.md). The target must be in Run mode.

## Topology (Automation Interface)

| Tool | Purpose |
|---|---|
| `ScanHardware(project?)` | Read the configured EtherCAT topology from the project: masters, terminals with slot and order number. No physical bus scan, no I/O interrupted |
| `ScaffoldHardwareCode(gvlName?, plcName?, project?)` | Generate a GVL of `VAR_GLOBAL` I/O declarations from the topology, named `Slot{N}_{OrderNumber}_{Channel}` |
| `AddEtherCatMaster(deviceName?, project?)` | Add an EtherCAT master to the I/O tree |
| `AddEtherCatBox(parentName, boxName, orderNumber, before?, project?)` | Add a coupler or terminal by Beckhoff order number; E-bus terminals nest under their coupler |
| `DeleteIoDevice(target, project?, confirmed)` | Remove a device or box, cascading its children |

`project` names the TwinCAT project to target and is required when the solution has more than one, so I/O never lands in the wrong project. Changes are saved to the `.tsproj` immediately.

`DeleteIoDevice` is destructive: the first call without `confirmed=true` returns a preview (the resolved tree path and the children that would cascade) and deletes nothing.

For a product's datasheet by order number, see [`FindHardware`](../docs-searcher/overview.md) instead.
