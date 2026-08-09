# Plan: SARIF output for `tckit analyse`

Working document. Delete when the work lands. Extends [ADR-0017](adrs/0017-static-analysis-and-naming-conventions.md);
fold the outcome into its Status notes and Current state, then remove this file.

## Goal

Emit SARIF 2.1.0 from `tckit analyse` so GitHub code scanning can ingest it via
`github/codeql-action/upload-sarif`. That buys inline annotations on the PR diff,
a Security tab with new-versus-existing history, and persistent dismissal UI.

## The hard part, and why it comes first

SARIF locates a result as **file URI + line number in that file**. Our findings are
located as `(ObjectName, ItemName, Part, Line)`, where `Line` is relative to one CDATA
block inside the `.TcPOU` XML. "Line 4 of `Execute`" is not line 4 of anything on disk.

This is the open question ADR-0017 deferred ("whether to also compute a real file line").
`--format text` never forced it because a human reads `FB_Host.Execute(4)` fine. SARIF
forces it, because GitHub needs a real path and line to hang an annotation on.

**Approach.** Parse with `LoadOptions.SetLineInfo` and read `((IXmlLineInfo)cdata).LineNumber`
off the `XCData` node. Content line 1 sits on the same line the `<![CDATA[` opener does,
so `absoluteLine = cdataLine + (finding.Line - 1)`. Verify that assumption against real
files rather than trusting it; handle the case where the CDATA opener is followed by a
newline before content.

**Where it lives.** The analysis adapter must not parse XML (it only sees `PouSource`),
so the reader captures the offsets and the analyser resolves them:

1. Reader populates line offsets on the DTOs.
2. `AnalysisFinding` gains `FilePath` and `FileLine`.
3. `ProjectAnalyser` resolves both after the rules run, where `FindingSuppressor` already sits.

Rules stay ignorant of file layout, and JSON and text output get real locations for free.

## Work items, in order

### 1. Line offsets in the reader

- `TcFileParser`: load with `LoadOptions.SetLineInfo`; return the CDATA line for each
  declaration and each implementation body.
- `PouSource` / `PouMember` gain `DeclarationLine` and `BodyLine` (1-based, line in the
  file where that CDATA's content starts). `Gvl` and `Dut` gain `DeclarationLine`.
- `PouSource` / `Gvl` / `Dut` already carry `Path`.

**Test that cannot silently drift:** for each finding on a real fixture, read the file,
split into lines, and assert `lines[fileLine - 1]` contains the finding's `Symbol`. That
self-validates the mapping against the actual bytes rather than against a hard-coded
expectation. Cover CRLF and LF, a method body, a property accessor (`Status.Get`), a GVL
and a DUT.

### 2. Locations on findings

- `AnalysisFinding` gains `FilePath` (repo-relative where possible) and `FileLine`.
- New `FindingLocator` in the analysis adapter, applied in `ProjectAnalyser` next to
  `FindingSuppressor`: map `(ObjectName, ItemName, Part)` to the right source and offset,
  the same keying `FindingSuppressor` already does. Consider sharing that index.
- Object-level findings (`Line = 1`, empty `ItemName`) point at the declaration start.
- A finding whose location cannot be resolved keeps `FileLine = 0` and is emitted without
  a `region`, rather than being dropped or given a wrong line.

### 3. Rule catalogue

SARIF wants `tool.driver.rules[]` with stable ids and descriptions. Rule metadata is
currently scattered as consts across `NamingRuleEngine` and `CorrectnessRules`.

- New `RuleCatalogue`: one entry per `TCK` id with category, default severity, short and
  full description, and a `helpUri` anchor into the analysis docs page.
- Both rule engines read their ids and default severities from it, so the catalogue is the
  single source of truth rather than a parallel list that drifts.
- This is a good forcing function: every rule gets a stable docs anchor.

### 4. SARIF writer

- New `SarifWriter` in the analysis adapter producing SARIF 2.1.0.
- Severity maps: `Error` to `error`, `Warning` to `warning`, `Suggestion` to `note`.
  `Silent`/`None` are already filtered before output.
- Reuse `AnalysisBaseline.Fingerprint` as `partialFingerprints["tckitFingerprint/v1"]`, so
  GitHub can track a finding across line moves. Nice reuse: the property that makes the
  baseline stable is exactly what SARIF wants here.
- Emit `originalUriBaseIds` and **repo-relative URIs**. Absolute Windows paths will not
  match GitHub's checkout, so this needs a base directory: `--sarif-base <dir>`, defaulting
  to the current working directory.

### 5. CLI

- `--format sarif` writing to stdout, so `> results.sarif` works.
- `--sarif-base <dir>` for the URI base.
- Exit-code behaviour unchanged.

### 6. CI

- Add an `upload-sarif` step to the workflow. **This is the only outward-facing piece**, in
  that findings become visible in the repo's Security tab. Flagging it as a decision rather
  than doing it silently.

### 7. Docs, changelog, ADR

- Analysis docs page: a SARIF section under "Running it in CI", with a copyable workflow snippet.
- CHANGELOG under the existing Added entry.
- ADR-0017: close the "real file line" open question, add a Status notes entry, update
  Current state. Delete this file.

## Verify against the corpus

`C:\twincat-corpus` holds TcOpen (510 POUs), TcUnit (79), TcUnit-Verifier (29) and
TwinCat-Dynamic-Collections (59), cloned with `core.longpaths` (the limit silently truncated
the first attempt to 12% of TcOpen).

- Run over TcOpen, then spot-check that reported file/line pairs really contain the symbol.
- Every previous defect in this feature was found by running on real code, not by unit tests.
  Budget for that again.
- GitHub caps a SARIF upload (currently 5,000 results per file). TcOpen's 1544 fits, a larger
  codebase may not, so note that `--baseline` is the answer rather than truncating silently.

## Open decisions

1. **Enable `upload-sarif` in this repo's CI?** It makes fixture findings public in the
   Security tab. Default: build the writer, leave the upload step commented with a note.
2. **Repo-relative paths when the project sits outside the repo?** Fall back to an absolute
   `file://` URI, which is still valid SARIF and useful locally, just not annotatable.

## Out of scope

Non-ST languages (settled). Auto-fix. SARIF `fixes[]`, which would need the rename guard
thought through and is a separate decision.
