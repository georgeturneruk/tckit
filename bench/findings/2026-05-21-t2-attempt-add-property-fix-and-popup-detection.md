---
date: 2026-05-21
status: Current
related_adrs: [0007, 0012]
---

# 2026-05-21 — T2-pid bench attempt: `add_property` end-to-end fix and XAE popup detection

First attempt to run T2-pid as a closed-loop bench. Two distinct problems
surfaced, both worth recording.

## 1. `add_property` had never run end-to-end against XAE

ADR-0012 status note (2026-05-18) reads "Both tools ship with payload-shape
unit tests... Bridge handlers smoke-tested via the T2-pid fixture-authoring
path (Commit D in the same PR)". The first half is correct; the second half
is not. `bench/fixtures/bug-hunting/_author/author_T2.py` only calls
`add_pou` and `add_method`; the eight PID properties are deliberately left
for the bench-arm LLM to author. So commit `c32dfd7` shipped the fixture
without ever exercising the bridge to COM property path.

A new smoke at `bench/fixtures/bug-hunting/_author/smoke_property.py`
runs the full round-trip against a throwaway project. It caught four
bugs in `bridge/harness/Add-TcProperty.ps1` and one in the reader. The
COM-side errors were progressive (each fix unblocked the next), which
is why they had stayed buried:

| Symptom                                                          | Cause                                                                                                | Fix                                                                                                       |
|------------------------------------------------------------------|------------------------------------------------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------|
| `Object reference not set to an instance of an object`           | `CreateChild` 4th arg (`vInfo`) was `$null`. TwinCAT dereferences null inside the COM call.          | Pass a `[string[]]` with `[language, return_type, access_modifier]` for FB, type string only for INTERFACE. |
| `Requested value 'LREAL' was not found`                          | After first fix, `vInfo` was a scalar string. TwinCAT parsed it as an `IECLanguageType`.             | Same as above; `vInfo` is always an array, never a scalar.                                                |
| `Item 'X' is deleted or invalidated by an earlier operation!`    | Property parent reference went stale after creating the first accessor; second `CreateChild` failed. | `Save-TcSolution` between Get and Set creates; re-find the parent via `LookupTreeItem` for each accessor. |
| Accessor names rejected on some XAE versions                     | Passing `'Get'` / `'Set'` as the `bstrName` argument.                                                | Always `''`; the kind constant (613/614/654/655) names the child.                                         |
| Reader returned empty `return_type` for round-tripped properties | Regex `PROPERTY\s+\w+\s*:\s*(\w+)` doesn't match `PROPERTY PUBLIC Foo : LREAL`.                      | Optional access-modifier group; same fix on `extract_method_return_type` for parallel coverage.           |

Source for the correct `vInfo` shape:
[Beckhoff/TC_AI_DOTNET_Samples — `GeneratePlcProject.cs` `AddProperty`](https://github.com/Beckhoff/TC_AI_DOTNET_Samples/blob/main/src/ScriptingContainer/Scripting.CSharp.Scripts/Scripts/GeneratePlcProject.cs).
Kind-constant verification:
[`ItemTypes.cs`](https://github.com/Beckhoff/TC_AI_DOTNET_Samples/blob/main/src/ScriptingContainer/ScriptingTestContainerBase/ItemTypes.cs).
Beckhoff InfoSys documents the method but not the per-kind vInfo
contract; the samples are the only authoritative reference for the
required array shape.

Interface-property authoring (kinds 612 / 654 / 655 with accessor
`vInfo=$null`) is implemented in `Add-TcProperty.ps1` for symmetry with
the `InterfaceMethod` branch added in `02b7abe`, but it is not yet
exercised by any smoke or bench fixture. T2-pid keeps properties on the
concrete FB precisely to dodge the empty-body issue under an INTERFACE
parent.

## 2. Bench cannot complete a writer-heavy arm in attach mode

Even with `add_property` working, the T2 tckit arm fails post-model-session
at the `reopen-sln` step:

```
/open (post-run reopen) failed: TcXaeShell DTE has no Solution object
(attached but uninitialised). Restart TcXaeShell and retry.
```

By the time the failure surfaces TcXaeShell is gone from the process
list. The isolated temp fixture is empty except for `.vs/` and a `.~u`
lock file. Whether XAE crashed mid-session or the model's tckit calls
destroyed the temp project is not yet established.

Root cause for this specific run: an unrelated TwinCAT licence-expired
modal dialog (`Target system reports a fatal error`, body
`'TwinCAT System' (10000): Error: >> license not found << checking
TwinCAT Licenses!`) blocked the XAE COM apartment. Subsequent
`Open-TcSolution` calls saw `DTE.Solution = $null` because the modal
preempted the assignment.

The Automation Interface itself surfaces no "I'm blocked" signal. There
is no `DTE.IsBlocked`, no `ITcSysManager.HasPendingDialog`. Symptoms
are inferred from `RPC_E_CALL_REJECTED` retries, `DTE.Solution=$null`,
or "Item has been deleted" errors that look like data bugs but are
actually apartment-level.

### Detection paths available

Three signals, in increasing order of intrusiveness:

1. **ADS pre-flight via TcXaeMgmt.** `Get-AdsState -Port 10000` (TwinCAT
   System service) and `Get-AdsState -Port 851` (PLC runtime) answer
   in under 50 ms with no DTE involvement. For the licence case the
   pattern is unambiguous: port 10000 returns `State=Config Succeeded=True`
   while port 851 returns `AdsErrorCode 6 (Target port not found)`.
   The licence dialog is the *result* of port 851 failing to come up;
   the upstream signal is on the wire seconds before the modal appears.
   `Get-TcLicense -NetId 127.0.0.1.1.1` returns entries but blank
   `Status` / `Expiration` fields on this Beckhoff release, so the
   port-851 absence is the more reliable signal.

2. **UI Automation window enumeration.** Win32 dialogs (class `#32770`)
   are top-level windows owned by the XAE process. Headless mode
   (`XAE_MODE=headless` plus `DTE.SuppressUI=$true`) hides the window
   but does NOT remove it from the window tree; `System.Windows.Automation`
   enumeration finds hidden dialogs identically to visible ones. The
   dialog's `Name` is the title, its descendant `Static` controls carry
   the body text, and `Button` descendants are the available actions.
   `InvokePattern.Invoke()` would dismiss if we wanted to, but
   licence-error dialogs specifically should fast-fail the bench rather
   than auto-dismiss; clicking OK without renewing the licence brings
   the next runtime call right back to the same error.

   `SuppressUI = $true` only catches Visual Studio shell dialogs.
   TwinCAT-runtime dialogs (raised by `TcSysSrv` / the local message
   subsystem and rendered by XAE as a courtesy) bypass it.

3. **System-message subscription.** `IAdsLogger` / the TwinCAT message
   router (port 10000 channel) broadcasts the underlying error that
   XAE later renders as a modal. Subscribing directly would catch the
   condition before any modal is constructed. Most ambitious; not
   needed for the licence case if the ADS pre-flight is in place.

### Suggested integration order

The cheapest defence is the ADS pre-flight; it covers the specific
licence failure mode and is one cmdlet call. The UI Automation scan
is a useful backstop for any modal class we haven't pre-flighted yet
(e.g. "an unhandled exception has occurred" debugger prompts) and works
identically in headless mode. The system-message subscription is the
most general but only worth the build cost if a meaningful set of
dialog classes proves to escape both layers above.

For T2 specifically, an ADS pre-flight in `run-bench.ps1` before the
pre-bench `/open` and again before the post-run `reopen-sln` would
turn this entire round into a clean "TwinCAT runtime licence is
invalid; cannot proceed" message rather than the cryptic
`DTE.Solution=$null` cascade.

## Status

`add_property` is now provably correct end-to-end and has a regression
net (`smoke_property.py`). The reader handles access-modifier
declarations. The bench refactor (data-driven `bench/run-bench.ps1`
plus per-fixture `bench-config.json` manifests) is in place to drop in
T2 once the licence and stability issues above are resolved. T2's
paired numbers remain pending.
