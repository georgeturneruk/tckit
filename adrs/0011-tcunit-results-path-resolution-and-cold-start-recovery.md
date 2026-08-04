---
adr: 0011
title: TcUnit results path resolution, run_tests inline failures, placeholder idempotency, save_plc_as_library cold-start retry
status: Implemented
created: 2026-05-17
last_reviewed: 2026-08-04
issue:
pr:
related: [0006, 0007, 0009, 0010]
---

## Current state

**Decision (live):** The six fixes shipped in the Python/bridge era and
carried into the C# rewrite (ADR-0015, PR #132), with post-rewrite
deviations: (1) results-path resolution now lives in `TcKit.Ads.TcUnitResults`
and is **target-aware** — the UmRT whose `TcRegistry.xml` declares the target
AMS Net ID owns the path, ahead of an existence ladder that checks the 4026
local-runtime boot root (`%ProgramData%\Beckhoff\TwinCAT\3.1\Boot`) before
the legacy kernel root (`C:\TwinCAT\3.1\Boot`), then the freshest UmRT
candidate; the no-match fallback also prefers the 4026 root when it exists
on disk (PR #134 for target-awareness, 2026-08-04 for the 4026 root, which
a laptop bench found missing — the ladder was pre-4026 only); (2) `RunTests`
returns `summary` + failures-only inline, plus `tests_passed` distinct from
infrastructure `success` (PR #134); (3) placeholder idempotency probe and
(6) `SetPlaceholderParameters` survive as C# verbs, now backed by the
ParameterGuard (PR #134); (4) the `SavePlcAsLibrary` cold-start retry is in
`ProjectAuthor`, and since 2026-08-04 the metadata round-trip preserves any
`ProjectInfo` Title/Company/Version the project already carries — only blank
fields are filled (Title←PLC name, Company←'Tc3 Project', Version←'1.0.0.0')
and a fully-populated `ProjectInfo` skips the `ConsumeXml` rewrite entirely;
(5) the `tckit doctor` TcUnit section did **not** survive — the doctor CLI
was not ported in the rewrite. Headless cold-start stays deferred
(ADR-0014/0015 territory).

**Where it lives:** `dotnet/src/TcKit.Ads/TcUnitResults.cs`,
`dotnet/src/TcKit.Adapters.Ads/RuntimeOperations.cs`,
`dotnet/src/TcKit.Adapters.Automation/ProjectAuthor.cs`. Original validation
in `bench/findings/2026-05-17-adr-0011-impl-and-t1-rebench.md` and
`bench/findings/2026-05-18-t1-friction-fixes-and-skill-nudges.md`; the
resolver and parameter lanes were re-verified live against a UmRT in PR #134.

**Open questions:**
- The N=3 T1 re-bench sweep was never run; the 2026-05-18 finding's variance
  characterisation stands unconfirmed. Rerun only if bench work resumes.

## Context

Two May-16 bench rounds drove this. The T1 schmitt-trigger TDD pair
showed the tckit arm costing **9x** vanilla on every metric (49 vs 7
calls, 17,667 vs 2,014 tokens, 385.1s vs 36.7s wall). Almost all of
that gap traced to a single bridge bug: `Get-TcUnitDefaultXmlPath`
only handled the kernel-RT boot folder, so on a UmRT bench the XML
was at `C:\ProgramData\Beckhoff\TwinCAT\3.1\Runtimes\UmRT_Default\
3.1\Boot\tcunit_xunit_testresults.xml` but the bridge looked at
`C:\TwinCAT\3.1\Boot\Plc\Port_851\…`. `get_test_results` returned
empty per-test data, the model couldn't see what was failing, and it
iterated through deploy + run cycles trying to find ground truth.

The same bench round (B1 off-by-one bug-hunt) re-surfaced a cold-start
`save_plc_as_library` failure (`XmlAutomationException ...
PlaceholderReference/EffectiveResolution`) and the `add_library_
placeholder` retrofit footgun where COM `AddPlaceholder` throws
"already contained!" on existing placeholders. Both forced operator
work-arounds documented in the T1 finding's "Hacked-around in this
round" section.

Self-validation stays the rule: the `tc-build-test-loop` skill keeps
its 5-iteration cap, its build-before-deploy ordering, and its
safety-handshake. The T1 finding's suggested "skip the loop if
confident from spec" off-ramp is explicitly **out of scope**. The fix
is to make self-validation fast and informative, not to skip it.

Findings:
[2026-05-16-t1-schmitt-trigger-pair](../bench/findings/2026-05-16-t1-schmitt-trigger-pair.md),
[2026-05-16-b1-bench-harness-tckit-smoke](../bench/findings/2026-05-16-b1-bench-harness-tckit-smoke.md).

## Decision

Four fixes, plus a `tckit doctor` section and a clarity-follow-on MCP
route, landing on one branch.

### 1. UmRT XML auto-detect

`Get-TcUnitDefaultXmlPath` becomes a fallback ladder, signature
unchanged:

1. `$env:TCKIT_TCUNIT_XML_PATH` (operator escape hatch, kept).
2. Kernel-RT: `C:\TwinCAT\3.1\Boot\Plc\Port_<port>\<filename>` if the
   file exists.
3. UmRT glob:
   `Join-Path $env:ProgramData "Beckhoff\TwinCAT\3.1\Runtimes\*\3.1\Boot\<filename>"`.
   - 1 candidate: returned.
   - >1 candidates: most-recently-modified returned, with a warning
     stashed on `$script:LastResolveWarning` and surfaced through a
     new `Get-TcUnitXmlResolveWarning` helper. Bridge responses
     include it in a new `resolve_warning` field.
4. Fallback: kernel-RT path string even if missing, so the existing
   "not found at <path>" error in `Get-TcUnitResults.ps1:195-196`
   keeps its stable shape.

The AMS Net ID cannot narrow UmRT candidates: `127.0.0.1.1.1` is
per-host, not per-runtime, and the runtime name lives only in the
on-disk path. mtime is the only reliable freshness signal on the
host side, and it is safe in practice because `Invoke-TcUnitRun.ps1`
waits on the XML file's mtime via `Wait-TcFileFresh` (the just-run
XML is always the freshest match).

A new `Resolve-TcUnitXmlCandidates` helper enumerates env override,
kernel path, and UmRT candidates with existence flags. Used by the
`tckit doctor` TcUnit section and the `/tcunit-xml-resolve` bridge
route.

### 2. run_tests returns failure-first summary inline

`Invoke-TcUnitRun.ps1` already blocks on `AllTestSuitesFinished` and
waits for the XML write, so the work is parser plumbing only.

New `ConvertFrom-TcUnitXml -XmlPath <p> -FailuresOnly <bool>` helper
factored out of `Get-TcUnitResults.ps1`. When `FailuresOnly` is true,
the suites list omits passing tests (so a 300-test all-green run
returns summary only; a 300-test red run returns only the failed
tests). Each failure carries `suite_name`, `test_name`, `message`;
file/line attributes are not surfaced (lean shape).

`Invoke-TcUnitRun.ps1` gains `[bool]$IncludeResults = $true`; on a
successful run with `xml_published` it merges `summary` and
`failures` into the response. The Python `run_tests` port gains
`wait_for_results: bool = True`; the adapter passes
`IncludeResults` through; the Result's `details` carries the inline
summary + failures. `get_test_results` still returns the full
per-test list (including passes) for callers that want it.

The `tc-build-test-loop` skill drops the prescribed
`get_test_results` call after `run_tests` on the happy path
(redundant once `run_tests` returns inline). All other discipline
rules stay.

### 3. add_library_placeholder idempotency

`Add-TcLibraryPlaceholder.ps1` probes the on-disk `.plcproj` before
the COM `AddPlaceholder` call via a new file-only
`Test-TcPlcProjHasPlaceholder -PlcProjPath -PlaceholderName` helper
(mirrors the XPath probe already in
`Set-TcPlcProjPlaceholderParameters`). If the placeholder is already
present, skip `AddPlaceholder` + `Save-TcSolution`; the parameter
splice block runs unchanged. Response includes
`details.already_present = $true` on the skip path.

### 4. save_plc_as_library cold-start retry

`Save-TcPlcAsLibrary.ps1` wraps the metadata round-trip
(`ProduceXml` / `ConsumeXml`) and `SaveAsLibrary` in a try. On
`XmlAutomationException ... PlaceholderReference/EffectiveResolution`,
invoke `Invoke-TcBuild.ps1` once against the same PLC project, then
retry. Any other exception rethrows unchanged. The retry path
populates `details.cold_start_warmup = $true`.

Always-build was rejected (would add 30-90s per call when warm). The
detect-and-recover shape only pays the cost when the catch-22
actually hits.

**Headless mode caveat:** when `XAE_MODE=headless`, the warm-up
build can be blocked by the Visual Studio Appid Stub SyncLock wedge
documented in the B1 finding. In that case the retry rethrows with
"save_plc_as_library cold-start retry requires attach mode; headless
mode blocked by SyncLock". Fixing headless is non-trivial and
deferred.

### 5. tckit doctor TcUnit XML section

`tckit/utils/diagnostics.py` gains `tcunit_xml_status()` calling a
new bridge route `/tcunit-xml-resolve` that reuses
`Resolve-TcUnitXmlCandidates`. `tckit/cli.py` wires the section into
`_doctor`. Outcomes:

- OK: env override set and file exists, or exactly one candidate
  resolves (kernel or single UmRT).
- WARN: multiple UmRT candidates. List them; recommend pinning via
  `TCKIT_TCUNIT_XML_PATH`.
- FAIL: zero candidates. Print kernel default and UmRT glob root
  searched.

Read-only diagnostic; never writes to config or env vars.

### 6. set_placeholder_parameters MCP route

Fix 3 makes `add_library_placeholder` idempotent, so it can also be
used for "the placeholder exists, only update its parameters". That
overloads the verb. A dedicated `set_placeholder_parameters` route
expresses the narrower intent (relevant for bench fixture retrofits
and library tuning).

## Alternatives considered

**Always-build before `save_plc_as_library`** (Fix 4 alt): rejected
on cost. The catch-22 is a cold-start phenomenon; paying ~30-90s on
every call when warm to dodge it is the wrong trade.

**Probe placeholder existence via COM** (Fix 3 alt): rejected on
clarity. `ITcPlcLibraryManager` has no clean "is placeholder X
present" predicate, and the parameter splice already operates on the
on-disk XML. One concern domain wins.

**Merge `run_tests` + `get_test_results` into one MCP tool** (Fix 2
alt): rejected on back-compat. The two-tool surface is documented
and other agents may call them directly. A `wait_for_results=True`
default on `run_tests` is the same ergonomics without breaking
external callers.

**Inline full results (passing tests included) in `run_tests`** (Fix
2 alt): rejected on context cost. A 300-test all-green run would
return all 300 inline for no benefit. Failure-first plus
`get_test_results` for the rare full-list need is the right split.

**Use AMS Net ID to narrow UmRT candidates** (Fix 1 alt): does not
work. The Net ID is per-host, not per-runtime; multiple UmRT
runtimes on the same host share `127.0.0.1.1.1`. mtime is the only
reliable on-host disambiguation.

## Consequences

**Enables:**

- T1 bench tckit arm should drop from ~49 calls to something close to
  vanilla's 7 once the bridge returns useful per-test detail on the
  first cycle.
- Bench fixture retrofits (TcUnit publisher enable, library param
  tuning) stop requiring direct PowerShell or hand-edits of
  `.plcproj`.
- Cold-start `save_plc_as_library` no longer needs the operator
  warm-up POU + method + build dance documented in the B1 finding.
- `tckit doctor` catches the kernel-vs-UmRT path-resolution problem
  before the bench runs.

**Costs:**

- MCP signature change on `run_tests` (`wait_for_results` parameter).
  Default-true preserves caller ergonomics; external callers that
  hand-roll polling can pass `False`.
- `TestResults` dataclass gains a `warning: str = ""` field.
- New `set_placeholder_parameters` MCP tool surface; one more verb to
  document.

**Locks us out of:**

- Treating the kernel-RT path as the sole "default" — the bridge now
  knows about UmRT as a first-class default, not a fallback.

## Status notes

- 2026-05-17: Drafted. Proposed.
- 2026-05-17: All six fixes landed on `feat/tcunit-self-validation`
  (UmRT auto-detect, `tckit doctor` TcUnit section, `run_tests`
  failure-first inline payload, `add_library_placeholder`
  idempotency, `set_placeholder_parameters` route,
  `save_plc_as_library` cold-start retry). 369 unit tests +
  combined Pester suites green. Promoted to Accepted. Re-bench T1
  pending (expected: tckit arm call count drops from 49 toward
  vanilla's 7 once the UmRT auto-detect + inline failures land
  together).
- 2026-08-03: Promoted to Implemented. All six fixes had long shipped and
  survived the C# rewrite except the doctor TcUnit section (doctor CLI not
  ported); the resolver was superseded by target-aware resolution and the
  parameter verbs hardened by the ParameterGuard in PR #134, which also
  re-verified the lane live against a UmRT. The T1 re-bench sweep that
  originally gated promotion never ran; promotion proceeds on shipped-and-
  re-verified grounds instead.
- 2026-08-04: Course-correction from a laptop bench round. The resolver's
  existence ladder only knew the pre-4026 kernel boot root, so on a 4026
  local runtime (which publishes under
  `C:\ProgramData\Beckhoff\TwinCAT\3.1\Boot`) it resolved
  `C:\TwinCAT\3.1\Boot\Plc\Port_851\…` and results were never found; the
  4026 root now sits ahead of the legacy root in both the existence checks
  and the no-match fallback. In the same round, `SavePlcAsLibrary` was
  found rewriting `ProjectInfo` on disk with hardcoded
  Company='Tc3 Project'/Version='1.0.0.0'; it now preserves existing
  values and only fills blanks.
