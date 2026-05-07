# Phase 2 Spike Notes

Spikes run on dev machine, 2026-05-07. Environment: Windows 11 Pro,
TwinCAT 3.1.4026, TcXaeShell Express, PowerShell 5.1.

**Bottom line: the original COM-based architecture works.** The plan's harness
scripts had several wrong assumptions about paths, kind constants, and source
write APIs, but each has a clean fix. No file-manipulation pivot needed.

References:

- [Accessing, creating and handling PLC projects](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242730891.html)
- [PLC POU creation index](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242732427.html)
- [ITcPlcIECProject](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242870539.html)
- [ITcSmTreeItem](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242779659.html)

---

## 1 — DTE attach ✅ PASS

`[Marshal]::GetActiveObject('TcXaeShell.DTE.17.0')` works.
`dte.Edition` reports `Express` on this install.

`Express` matters: `dte.ToolWindows.ErrorList` and `dte.Windows.Item(<output>).Object`
return null in Express. Build-error retrieval needs an alternative path —
see section 6.

---

## 2 — Project creation ✅ PASS (4-step recipe)

Validated working sequence to build a fresh PLC solution from scratch:

```powershell
$dte.Solution.Create($outDir, $name)
$dte.Solution.AddFromTemplate($tspprojTemplatePath, $outDir, $name, $false)
$tipc = $dte.Solution.Projects.Item(1).Object.LookupTreeItem('TIPC')
$plc  = $tipc.CreateChild('MyPlc', 0, $null, 'Standard PLC Template.plcproj')
$dte.Solution.SaveAs("$outDir\$name.sln")
```

Key discoveries:

- **Template file path** on a fresh install:
  `C:\Program Files (x86)\Beckhoff\TwinCAT\3.1\Components\Base\PlcTemplate\TwinCAT PLC Project.tspproj`
  (file extension `.tspproj`, not `.tsproj` — 4026 introduces this PLC-only project type).
- `dte.Solution.GetProjectTemplate(...)` is **not present** on TcXaeShell Express. Pass the
  template path directly to `AddFromTemplate`.
- For `tipc.CreateChild` to add a PLC sub-project: the **kind is 0** and the 4th arg
  is the template name `'Standard PLC Template.plcproj'`. The other CreateChild kinds
  (604/602/...) are for items inside a PLC project.
- 3rd and 4th args to `CreateChild` must be `$null` when not used; passing `''`
  raises `ArgumentException: Must specify valid information for parsing in the string`.

---

## 3 — Tree navigation into PLC source ✅ PASS

**Critical path discovery:** the path uses the PLC project name **twice**, with
` Project` appended to the second occurrence:

```
TIPC^<PlcName>^<PlcName> Project^POUs            # the POUs folder
TIPC^<PlcName>^<PlcName> Project^POUs^<POUName>  # an existing POU
TIPC^<PlcName>^<PlcName> Project^GVLs            # GVLs folder
TIPC^<PlcName>^<PlcName> Project^DUTs            # DUTs folder
```

Without the ` Project` suffix on the second occurrence, lookup fails with
`Subitem '...' under nested project '<PlcName>' not found`. Confirmed against
[the official sample on infosys](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242730891.html).

Also: `LookupTreeItem('TIPC^MyPlc')` returns the PLC system-manager node
(ItemType 56). Its only child is the PLC instance (`TcUnit Instance`-style,
ItemType 57). The instance is **not** the path to source — use the doubled-name
form above.

---

## 4 — Adding a POU and writing source ✅ PASS

Working pattern:

```powershell
$pous = $sm.LookupTreeItem('TIPC^MyPlc^MyPlc Project^POUs')
$fb = $pous.CreateChild('FB_Test', 604, $null, $null)
$fb.DeclarationText   = "FUNCTION_BLOCK FB_Test`r`nVAR_INPUT`r`nbX : BOOL;`r`nEND_VAR"
$fb.ImplementationText = "; // body here"
$method = $fb.CreateChild('DoStuff', 609, $null, $null)
$method.DeclarationText   = "METHOD DoStuff : BOOL"
$method.ImplementationText = "DoStuff := TRUE;"
```

`DeclarationText` / `ImplementationText` come from the `ITcPlcDeclaration` /
`ITcPlcImplementation` interfaces. **PowerShell COM IDispatch finds them
automatically** — no explicit `[Marshal]::QueryInterface` cast needed.

`ImplementationXml` and `Implementation.Language` properties also exist (latter
set to `null` on default-language items).

---

## 5 — Tree-item kind constants (corrected) ⚠️

The plan's harness was using best-guess values that were **mostly wrong**.
Authoritative table from [infosys](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242732427.html):

| Item | Kind |
|------|------|
| Folder | 601 |
| Program | 602 |
| Function | 603 |
| Function Block | 604 |
| Enum | 605 |
| Struct | 606 |
| Union | 607 |
| Action | 608 |
| Method | 609 |
| Interface Method | 610 |
| Property | 611 |
| Interface Property | 612 |
| Property Get | 613 |
| Property Set | 614 |
| GVL | 615 |
| Transition | 616 |
| Interface | 618 |
| POU Template Import | 58 |
| **PLC project (under TIPC)** | **0** (with template name in 4th arg) |

What our code had right: `FunctionBlock = 604`. Wrong: Method (we had 603 →
should be 609), Action (606 → 608), Property (607 → 611), Program (605 → 602),
Function (602 → 603), Interface (615 → 618). GVL was 615 in the table; we
didn't have it.

---

## 6 — Build + structured errors ⚠️ NEEDS HYBRID APPROACH

In TcXaeShell Express:

- `$dte.Solution.SolutionBuild.Build($true)` triggers a **synchronous** build of
  the whole solution. Returns when done.
- `$dte.Solution.SolutionBuild.LastBuildInfo` → `0` on success, non-zero count
  of failed projects on failure. **However, this only reports SystemManager
  build state — it returns `0` (success) even when PLC source has compile errors.**
- `$plcProj.CheckAllObjects()` (callable on the `TIPC^<plc>^<plc> Project` node)
  triggers a **PLC source compile** and returns `Boolean`. Returns `False` when
  there are PLC errors, `True` when clean. **This is the actual PLC-build signal.**
- `$dte.ToolWindows.ErrorList` and `$dte.Windows.Item(<output>).Object` are
  `null` in Express — so per-error structured data isn't reachable through DTE.
- `$sm.GetLastErrorMessages()` returns a `Count`-1 collection, but messages were
  empty in our spike. Likely intended for SystemManager-level errors.

To get **structured per-error data** (file/line/message/severity) the validated
recipe is to invoke `devenv.exe` from outside the COM call:

```
devenv.exe <sln> /rebuild "<config>|<platform>" /log <path>\Log.xml
```

`devenv.exe`'s `/log` writes a structured XML log (with errors/warnings) that we
parse on the harness side. This works on Express. (Sourced from Beckhoff's
`benhar-dev/batch-build-twincat-project` sample.)

**Recommended hybrid for `BuildRunner.build`:**
1. Open solution + call `CheckAllObjects()` for fast in-process binary signal.
2. If that returned False (or always, depending on caller), invoke `devenv.exe /log`
   to produce a parseable error log, then read+parse the XML.

Both paths can live inside `Invoke-TcBuild.ps1`.

---

## 7 — `??` operator and other PS 5.1 issues ✅ FIXED

- `??` (PS 7+) replaced with `$(if (...) {...} else {...})` everywhere.
- `($obj.GetType().Name -match 'TcSysManager')` always matches `System.__ComObject`
  for COM objects — replaced with "try `LookupTreeItem('TIPC')`" probe.
- Both fixes are already in the code.

---

## 8 — Other gotchas captured along the way

- `Solution.Open(<sln>)` is **slow** for non-trivial solutions (TcUnit's main
  `.sln` had not returned after 60s). The bridge `/build` and `/open` endpoints
  must accept very long timeouts.
- `Solution.Open` followed by `Close($false)` discards in-memory `CreateChild`
  calls that haven't been flushed via `SaveAs`. Order matters in tests.
- TcUnit's structure (top-level `.tsproj` referencing nested `.xti` referencing
  nested `.plcproj`) loaded enough through `LookupTreeItem('TIPC')` for the
  System Manager tree to be visible, but the inner POU items showed
  "Hidden subitem ... not found" until we used the doubled-name path above.
  Conclusion: TcUnit's path would be `TIPC^TcUnit^TcUnit Project^POUs^FB_Test`
  (we did not re-test against TcUnit, but the pattern is consistent with infosys).

---

## Required harness changes

Recap of the concrete code edits needed in `bridge/harness/`:

1. **`_TcDte.psm1`**: kind table corrections (table in section 5).
2. **`_TcDte.psm1`**: drop `Get-TcPlcProject`'s "first PLC project under TIPC"
   helper — replace with "find the `<plc> Project` node under
   `TIPC^<plc>^<plc> Project`". Probably accept the PLC name as an explicit
   argument from the caller.
3. **`Set-TcItemContent`**: throw out the `ConsumeXml` splice — write
   `$item.DeclarationText` and `$item.ImplementationText` instead. Accept
   declaration / implementation as separate inputs (or split a combined input).
4. **`Add-TcPou.ps1` / `Add-TcMethod.ps1`**: use the correct path + kind, and
   pass `$null, $null` for CreateChild's 3rd/4th args.
5. **`Invoke-TcBuild.ps1`**: replace the DTE-only build with the hybrid pattern
   from section 6 — call `CheckAllObjects` for the binary signal, optionally
   shell to `devenv.exe /log` to harvest structured errors, parse the XML log.
6. **Drop `_TcDte.psm1`'s ErrorList helper.** Replace with a `Parse-DevenvLog`
   helper that reads `Log.xml` and yields `@{file; line; message; severity}`.
7. **Adapter / bridge_client / Python tests** all stay as-is — none of these
   findings invalidate the HTTP layer.

The original plan's architecture survives the spike. Only the harness internals
need a focused rewrite.
