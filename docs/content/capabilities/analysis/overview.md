# Analysis

Static analysis of a TwinCAT project against a configurable convention. Like the reader, it parses the project files directly from disk: no XAE, no TwinCAT install, no licence, no running runtime. It is far cheaper than a build, so run it first.

| Tool | Returns |
|---|---|
| `AnalyseProject(projectPath, plcName?, objectName?, severity?, ruleIds?)` | Findings with a rule id, location and suggested name |

`projectPath` is the solution root directory or a `.sln` file inside it. `severity` is the floor for what comes back (`error`, `warning`, `suggestion`); `ruleIds` is a comma-separated allowlist such as `TCK1002`.

Pass `objectName` to check a single POU, GVL or DUT. That is the intended use while writing code: after editing a POU, analyse just that POU and fix what comes back before moving on.

## Running it in CI

Analysis needs no XAE, no licence and no runtime, so unlike `Build` it runs anywhere, including a Linux runner. The CLI carries the same analyser as the MCP tool:

```bash
tckit analyse <path> --severity warning --format text --fail-on warning
```

Exit codes are `0` clean, `2` findings at or above `--fail-on`, and `1` for a tool error, so a broken run is never mistaken for a failing one. `--format text` prints one finding per line in the `location(line): severity code: message` shape compilers use, which CI log parsers and editors already turn into annotations.

### Adopting it on an existing codebase

A mature project will report plenty on the first run: TcOpen reports 1544. Nobody is going to fix those before turning the check on, and a gate nobody can turn on never runs. So record them once and enforce from there:

```bash
tckit analyse <path> --severity warning --write-baseline tckit-analysis.baseline
```

Commit that file, then gate on it:

```bash
tckit analyse <path> --severity warning --fail-on warning --baseline tckit-analysis.baseline
```

The build now fails only on findings that are not in the baseline. Fingerprints deliberately exclude the line number, so inserting a variable higher up a declaration does not invalidate every entry below it and fail a build that changed nothing relevant. Delete a line from the baseline to start enforcing that finding again; `#` comments are ignored, so you can record why one is still there.

## Rules

Correctness rules default to `warning`, naming and structure to `suggestion`. Asking for `severity: "warning"` is how you say "only the things that are actually wrong".

### Correctness

Each of these compiles cleanly, which is the whole reason they are here.

| Id | Catches |
|---|---|
| `TCK2001` | A function block that must persist between calls, declared on a call stack (a method's `VAR`, or a `FUNCTION`). It is rebuilt every call and never advances. Use `VAR_INST`, or declare it on the function block. |
| `TCK2002` | `REAL` or `LREAL` compared with `=` or `<>`. Usually appears to work until a value is not exactly representable. |
| `TCK2003` | `RETAIN` or `PERSISTENT` on a local, where it cannot survive a restart. |
| `TCK2004` | A local declared and never used. |
| `TCK2005` | A function block input nothing ever reads, which normally means a wiring mistake. |

### Structure

| Id | Catches |
|---|---|
| `TCK3001` | A global written from more than one POU. On separate tasks that is a race; on one task the last writer in scan order silently wins. |
| `TCK3002` | A POU nothing instantiates, calls, or binds to a task, searched across every PLC project in the solution. |

### Naming

| Id | Covers |
|---|---|
| `TCK1001` | POU, DUT and GVL names |
| `TCK1002` | Variable names in any VAR block |
| `TCK1003` | Method, property and action names |
| `TCK1004` | Struct fields and enumeration constants |
| `TCK1005` | A type prefix left behind under a convention that does not use one |

`TCK1005` covers a gap the others structurally cannot: `nCount` is already valid camelCase, so a casing rule never notices the `n`. Three things keep it precise. The prefix must agree with the declared type, so `nCount : INT` is reported and `nextValue : INT` is not. It fires only on names that otherwise conform, so a name failing the casing rule is reported once rather than twice. And a variable named after its own type is left alone: `aDINT : ARRAY OF DINT` is the type under test, not a tagged variable.

Every finding carries the object, the item, the line within that item, and the offending identifier. Naming findings also carry a suggested name.

Suggestions are advisory. Nothing is rewritten for you, because renaming a symbol that is referenced elsewhere is a change you should agree to rather than discover.

### What the rules will not do

Each rule carries a guard, because a false positive is worse than a miss: it invites a "fix" that breaks working code.

- `TCK2001` only fires for instances that genuinely cannot work on a stack: a standard stateful block (`TON`, `R_TRIG`, `CTU` and friends), or a function block with a `Busy`/`Done` handshake, where the caller is expected to keep calling until it finishes. Declaring a synchronous helper local to one method is a common and correct idiom, and flagging it produced 18 false positives on TcUnit alone.
- Names TwinCAT mandates are never reported: `MAIN`, and anything inside `FB_init`, `FB_exit` or `FB_reinit`, whose parameters the compiler matches by name.
- An unimplemented stub is not reported for unused locals or unread inputs. It reads none of them by definition.
- `TCK2005` is skipped when anything extends the function block, since a child may be the reader.
- `TCK2004` covers locals only. TwinCAT 3 leaves a function block's `VAR` members reachable from outside, so an apparently unused one may be part of its API.
- `TCK3001` detects qualified writes (`GVL_State.Mode := 1`). It under-reports rather than guessing at unqualified access, which would need shadowing analysis.
- `TCK2002` reads simple operands only. A comparison through a dotted path is left alone.
- `TCK3002` cannot tell dead code from a library intended for consumers outside the solution, which is why it is a suggestion rather than a warning.

- `TCK3002` counts a namespace-qualified reference (`LibraryPlc.F_Trim`) as a use, which is how one PLC project calls into another. A library consumed only by a sibling test project is reached, not dead.
- `TCK3002` stands down entirely on a project containing Ladder, FBD, SFC, CFC or IL. Only ST is stored as readable source, so a call made from a ladder network is invisible, and reporting dead code on that evidence would be wrong. The result says so in `rules_not_run`. The body-scanning rules are unaffected in the dangerous direction: they under-report rather than inventing findings.

`TCK2005`, `TCK3001` and `TCK3002` need the whole solution in view, so they are skipped when `objectName` scopes the run, as is the `infer` profile. The result says which in `rules_not_run` rather than letting a partial pass look clean.

## Profiles

Pick a house style with `tckit_analysis_profile`:

| Profile | Objects | Variables |
|---|---|---|
| `hybrid` (default) | `FB_Motor`, `F_Clamp`, `PRG_Main`, `I_Drive`, `ST_Config`, `E_State`, `GVL_Parameters` | `Enable`, `_retryCount`, `retries`, `MaxRetries` |
| `dotnet` | `Motor`, `Clamp`, `Main`, `IDrive`, `Config`, `State`, `Parameters` | as `hybrid` |
| `hungarian` | as `hybrid` | `bEnable`, `nRetryCount`, `fbDrive`, `stConfig` |
| `infer` | learned from the project | learned from the project |
| `none` | not checked | not checked |

`infer` derives the convention from the project's own declarations and reports departures from what it already does, rather than from a table someone else chose. That makes it the honest option for an existing codebase whose house style matches none of the above: adopting it does not produce thousands of findings on day one.

It learns type prefixes per type family and capitalisation and scope prefixes per kind of declaration, treating those separately because a project that writes `bEnable` uses `b` in every VAR block but may still case an input differently from a local. Nothing is inferred from thin evidence: below three declarations, or below sixty per cent agreement, a slot simply gets no rule and the analyser stays quiet rather than enforcing a coincidence. Because it needs the whole project to learn from, `infer` is skipped on an `objectName`-scoped run and says so in `rules_not_run`.

`hybrid` is the default because the two halves of the Hungarian convention are worth separating. Kind prefixes on objects earn their place: POUs, DUTs and GVLs share one flat namespace, and a bare `Config` does not tell you whether it is a struct, an enum or a function block. Type prefixes on variables do not: the type is already in the declaration, and the prefix goes stale the moment the type changes.

It is also the cheapest to adopt. Object renames cross project boundaries and show up in the project tree; variable names are mostly local to one FB or one method. `hybrid` puts the churn where it is cheapest and leaves the expensive renames alone.

Under `hybrid` and `dotnet`, variables follow .NET conventions:

| Where | Convention | Example |
|---|---|---|
| FB `VAR_INPUT` / `VAR_OUTPUT` / `VAR_IN_OUT` | PascalCase | `Enable`, `ErrorId` |
| FB `VAR` / `VAR_STAT` | `_camelCase` | `_state`, `_retryCount` |
| Method parameters and locals | camelCase | `value`, `retries` |
| `VAR CONSTANT` | PascalCase | `MaxRetries` |
| `VAR_GLOBAL` | PascalCase | `CycleTime` |

A `FUNCTION`'s `VAR_INPUT` is a parameter list, not an instance surface, so it follows the parameter convention. `MAIN` is never flagged.

## Configuration

Configuration lives in the project's own `.editorconfig`, under a section that matches TwinCAT source files. That gives you per-folder overrides and the usual `root = true` search for free, and keeps the settings away from any .NET tooling in the same tree.

```ini
[*.{TcPOU,TcGVL,TcDUT}]
tckit_analysis_profile = hybrid

# Turn the whole naming category down, or up
tckit_analyzer_diagnostic.category-naming.severity = suggestion

# Or one rule at a time
tckit_diagnostic.TCK1002.severity = warning
```

A vendored library folder can opt out on its own:

```ini
[vendor/**.{TcPOU,TcGVL,TcDUT}]
tckit_analysis_profile = none
```

### Suppressing a finding

Every rule has legitimate exceptions. A `nearlyEqual` implementation opens with an exact float comparison on purpose, and `TCK2002` is right to notice it. Say so in the code:

```iecst
// tckit-disable-next-line TCK2002
IF Coordinate1 = Coordinate2 THEN

IF a = b THEN // tckit-disable-line TCK2002
```

Rule ids are comma-separated, and omitting them suppresses every rule on that line. Without this the only options are silencing a whole rule or living with the noise, and both end with the analyser being ignored.

### Custom rules

If none of the profiles fit, define your own in three parts: which symbols, what style, and at what severity. A style is defined once and can be reused by any number of rules.

```ini
[*.{TcPOU,TcGVL,TcDUT}]
tckit_naming_symbols.globals.applicable_kinds = variable
tckit_naming_symbols.globals.applicable_sections = var_global

tckit_naming_style.screaming.capitalization = all_upper

tckit_naming_rule.globals_screaming.symbols = globals
tckit_naming_rule.globals_screaming.style = screaming
tckit_naming_rule.globals_screaming.severity = warning
```

Symbol groups select on `applicable_kinds`, `applicable_sections`, `applicable_scopes` (`object` or `member`), `applicable_accessibilities`, `applicable_types`, and `required_modifiers`. Styles set `capitalization`, `required_prefix`, `required_suffix` and `word_separator`. Rules do not need ordering: the most specific match wins.

Configuration that cannot be applied comes back in `config_warnings` rather than being silently ignored, as do objects that could not be parsed.

## Why this shape

The compiler already tells you what does not build. Analysis only earns its place in the gap between "compiles" and "correct", so a rule ships only if `Build` would not have caught it. A green build is therefore not evidence against a finding.

Because the whole solution is parsed at once, the structure rules can ask questions a per-file checker cannot: whether a global has two writers, whether anything anywhere reaches a POU. A library consumed only by a sibling test project counts as reached.
