# Analysis

Static analysis of a TwinCAT project against a configurable convention. Like the reader, it parses the project files directly from disk: no XAE, no TwinCAT install, no licence, no running runtime. It is far cheaper than a build, so run it first.

| Tool | Returns |
|---|---|
| `AnalyseProject(projectPath, plcName?, objectName?, severity?, ruleIds?)` | Findings with a rule id, location and suggested name |

`projectPath` is the solution root directory or a `.sln` file inside it. `severity` is the floor for what comes back (`error`, `warning`, `suggestion`); `ruleIds` is a comma-separated allowlist such as `TCK1002`.

Pass `objectName` to check a single POU, GVL or DUT. That is the intended use while writing code: after editing a POU, analyse just that POU and fix what comes back before moving on.

## Rules

Correctness rules default to `warning`, naming and structure to `suggestion`. Asking for `severity: "warning"` is how you say "only the things that are actually wrong".

### Correctness

Each of these compiles cleanly, which is the whole reason they are here.

| Id | Catches |
|---|---|
| `TCK2001` | A function block instance declared in a method's `VAR`, or in a `FUNCTION`. It sits on the call stack, so it is reconstructed every call and any timer, edge detection or internal state silently resets. Use `VAR_INST`, or declare it on the function block. |
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

Every finding carries the object, the item, the line within that item, and the offending identifier. Naming findings also carry a suggested name.

Suggestions are advisory. Nothing is rewritten for you, because renaming a symbol that is referenced elsewhere is a change you should agree to rather than discover.

### What the rules will not do

Each rule carries a guard, because a false positive is worse than a miss: it invites a "fix" that breaks working code.

- An unimplemented stub is not reported for unused locals or unread inputs. It reads none of them by definition.
- `TCK2005` is skipped when anything extends the function block, since a child may be the reader.
- `TCK2004` covers locals only. TwinCAT 3 leaves a function block's `VAR` members reachable from outside, so an apparently unused one may be part of its API.
- `TCK3001` detects qualified writes (`GVL_State.Mode := 1`). It under-reports rather than guessing at unqualified access, which would need shadowing analysis.
- `TCK2002` reads simple operands only. A comparison through a dotted path is left alone.
- `TCK3002` cannot tell dead code from a library intended for consumers outside the solution, which is why it is a suggestion rather than a warning.

`TCK2005`, `TCK3001` and `TCK3002` need the whole solution in view, so they are skipped when `objectName` scopes the run. The result says which in `rules_not_run` rather than letting a partial pass look clean.

## Profiles

Pick a house style with `tckit_analysis_profile`:

| Profile | Objects | Variables |
|---|---|---|
| `hybrid` (default) | `FB_Motor`, `F_Clamp`, `PRG_Main`, `I_Drive`, `ST_Config`, `E_State`, `GVL_Parameters` | `Enable`, `_retryCount`, `retries`, `MaxRetries` |
| `dotnet` | `Motor`, `Clamp`, `Main`, `IDrive`, `Config`, `State`, `Parameters` | as `hybrid` |
| `hungarian` | as `hybrid` | `bEnable`, `nRetryCount`, `fbDrive`, `stConfig` |
| `none` | not checked | not checked |

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
