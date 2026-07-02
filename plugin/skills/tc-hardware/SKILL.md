---
name: tc-hardware
description: Use when inspecting or configuring TwinCAT hardware through TcKit — EtherCAT master/slave diagnostics, IPC hardware health, NC axis state, reading/writing live PLC symbols, invoking RPC methods, or authoring the I/O tree (scan the bus topology, scaffold I/O GVLs, add/remove EtherCAT masters and boxes). Triggers on requests like "what's the EtherCAT bus status", "list the EtherCAT masters", "check the IPC CPU/temperature/UPS", "what state is axis 1 in", "read MAIN.nCounter on the target", "set GVL.bEnable to TRUE", "call the Reset method on fbPid", "scan the hardware", "scaffold I/O variables", "add an EK1100 under the master", "delete Box 3". Do NOT use for reading/writing ST code (tc-write-st), build/deploy/test (tc-build-test-loop), or looking up a product's datasheet by order number (tc-beckhoff-docs / FindHardware).
allowed-tools: mcp__tckit__ListEtherCatMasters, mcp__tckit__GetEtherCatStatus, mcp__tckit__GetIpcHardware, mcp__tckit__ListAxes, mcp__tckit__GetAxisState, mcp__tckit__ReadSymbols, mcp__tckit__WriteSymbols, mcp__tckit__InvokeRpc, mcp__tckit__ScanHardware, mcp__tckit__ScaffoldHardwareCode, mcp__tckit__AddEtherCatMaster, mcp__tckit__AddEtherCatBox, mcp__tckit__DeleteIoDevice
---

# Hardware diagnostics, symbol I/O, and I/O authoring

Three distinct lanes, each with a different transport and precondition. Pick the lane that matches the request before calling anything.

| Lane | Tools | Transport / precondition |
| ---- | ----- | ------------------------ |
| **Diagnostics** (read-only) | `ListEtherCatMasters`, `GetEtherCatStatus`, `GetIpcHardware`, `ListAxes`, `GetAxisState` | ADS to a **running** target by AMS Net ID. No XAE. |
| **Symbol I/O** | `ReadSymbols`, `WriteSymbols`, `InvokeRpc` | ADS to a target in **Run mode** by AMS Net ID. No XAE. |
| **I/O authoring** (COM) | `ScanHardware`, `ScaffoldHardwareCode`, `AddEtherCatMaster`, `AddEtherCatBox`, `DeleteIoDevice` | XAE open with a solution loaded (the writer constraint). Reads/edits the configured project, not the physical bus. |

## Where `targetAmsId` comes from

The diagnostics and symbol-I/O tools take `targetAmsId` (the target's AMS Net ID). If you don't have it, ask the user — it's the same target you'd deploy to. Don't guess or hunt the filesystem.

## Diagnostics (read-only — no confirmation needed)

1. `ListEtherCatMasters(targetAmsId)` — enumerate masters (most systems have one). Use the returned NetId as `masterNetId` for the next call.
2. `GetEtherCatStatus(targetAmsId, masterNetId="")` — master diagnostic flags plus the slave table (state machine, identity, link health, per-port CRC counters). `masterNetId` defaults to `targetAmsId` (the usual single-master layout).
3. `GetIpcHardware(targetAmsId)` — TwinCAT version, CPU (temperature / usage / frequency), memory, fans, network adapters, UPS. Modules not present come back null/empty — report that, don't infer failure.
4. `ListAxes(targetAmsId)` / `GetAxisState(targetAmsId, axisId)` — NC axis state (name, error code, position, velocity, lag error, derived state name). `ListAxes` is empty when no NC axes are configured.

These read live state and never change anything; call them directly and report with the fields that answer the question.

## Symbol I/O — the confirmed-write handshake

- `ReadSymbols(targetAmsId, paths)` is read-only: call directly. Best-effort — an unreadable path comes back `null` in `values` rather than failing. The target must be in Run mode (symbols resolve at port 851).
- `WriteSymbols(targetAmsId, writesJson, confirmed)` and `InvokeRpc(targetAmsId, symbolPath, methodName, paramsJson, confirmed)` **mutate / execute live PLC state** and gate on `confirmed=true` (the same safety contract as deploy):
  1. First call without `confirmed` returns an error telling you confirmation is required. Treat it as a control-flow signal, not a failure.
  2. Surface to the user **exactly** what you are about to write/invoke and on which target. Wait for explicit approval in chat.
  3. Only then call again with `confirmed=true`.
  4. Never auto-confirm a write or RPC against a live PLC.
- `writesJson` is a JSON object of `path -> value` (e.g. `{"MAIN.nSetpoint": 42, "GVL.bEnable": true}`); writes are best-effort, with per-symbol failures in `details.errors` and `success` true only when every write landed.
- `InvokeRpc` targets a method decorated `{attribute 'TcRpcEnable'}`; `paramsJson` is a JSON array of positional parameters in `VAR_INPUT` order.

## I/O authoring (COM — XAE must be open)

**Every authoring/scan verb takes `project` (the TwinCAT project name).** A solution can hold more than
one TwinCAT project, each with its own I/O tree. If you omit `project` when several exist, the verb is
**refused** and lists the available projects — it will not guess (silently landing I/O in the wrong
project, e.g. GLR_Hardware, is exactly the failure this guards against). With a single-project solution
`project` is optional. **Run `ScanHardware` first** — it reports the resolved `project` names so you know
what to pass. Every write is **saved to that project's `.tsproj` on disk immediately** (no Build needed).

1. **Scan before you change.** `ScanHardware(project?)` reads the configured EtherCAT topology (each
   master with its terminals: slot, full tree name, order number) plus the resolved project name. No
   physical bus scan, so no I/O is interrupted. Use it to discover the project names and real tree names.
2. **Scaffold I/O code.** `ScaffoldHardwareCode(gvlName="HardwareIO", plcName, parentFolder, project?)`
   writes a `VAR_GLOBAL` GVL of I/O declarations from `project`'s topology (variables named
   `Slot{N}_{OrderNumber}_{Channel}`; unknown terminals get a comment placeholder). `project` selects
   which TwinCAT project's I/O to scan; `plcName` selects the PLC project the GVL is added to.
3. **Add a master.** `AddEtherCatMaster(deviceName="Device 1 (EtherCAT)", project?)`.
4. **Add a coupler/terminal.** `AddEtherCatBox(parentName, boxName, orderNumber, before="", project?)`.
   E-bus terminals (EL...) nest under their coupler, so `parentName` is the coupler (e.g. `Box 1
   (EK1100)`); EtherCAT-native slaves go directly under the master. `orderNumber` may be
   revision-qualified (`EL1008`, `EK1100-0000-0017`). `before` names the sibling to insert before (empty
   appends). If you don't know the order number's specs, look it up first via `tc-beckhoff-docs`
   (`FindHardware`).
5. **Delete — gated + previewed.** `DeleteIoDevice(target, project?, confirmed=false)` removes a device
   or box and **cascades its children**. `target` is a display name that must be **unique within the
   project** (an ambiguous name is refused, listing the candidate `^`-paths) **or** an exact
   `^`-delimited path for precision. The first call (`confirmed=false`) returns a **preview** — the
   resolved path and the child items that will cascade — and deletes nothing; call again with
   `confirmed=true` to commit. Surface the preview to the user and get approval before confirming.

### Authoring guards

- **Name the project.** In a multi-project solution, always pass `project` (from `ScanHardware`). A
  success result echoes the resolved `project` — check it landed where you intended.
- **Scan first.** Don't add or delete by guessing tree names — `ScanHardware` gives you the real project
  names and `parentName` / device-name strings to target.
- **Delete is a two-step handshake.** Show the user the preview (path + cascade), get approval, then
  re-call with `confirmed=true`. Prefer the exact `^`-path when a name is ambiguous. (True undo is git;
  there is no tckit un-delete.)
- **Safety-critical hardware.** If the change touches a safety device (FSoE / EL69xx / TwinSAFE), STOP
  and get explicit user approval first, the same as the safety-name guard in `tc-write-st`.

## Anti-patterns

- Calling `WriteSymbols` / `InvokeRpc` with `confirmed=true` without first showing the user the target and the exact write/call.
- Using the COM authoring tools (`ScanHardware`, `Add*`) against a target by AMS Net ID — they operate on the open XAE solution, not a remote runtime. Conversely, using the ADS diagnostics tools to inspect a project that isn't running.
- Concluding "TcKit isn't working" because a COM authoring tool failed (XAE not open) while an ADS diagnostics tool worked (or vice versa) — they use different transports.
- Reporting an absent IPC module (null UPS, empty fans) as a fault. Absent ≠ failed.

## Next

- To look up a terminal/box datasheet by order number, hand off to `tc-beckhoff-docs` (`FindHardware`).
- To write the ST that consumes scaffolded I/O, hand off to `tc-write-st`.
- To build/deploy/test after an I/O change, hand off to `tc-build-test-loop`.
