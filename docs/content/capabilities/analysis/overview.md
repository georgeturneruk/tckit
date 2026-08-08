# Analysis

Static analysis of a TwinCAT project against a configurable convention. Like the reader, it parses the project files directly from disk: no XAE, no TwinCAT install, no licence, no running runtime. It is far cheaper than a build, so run it first.

| Tool | Returns |
|---|---|
| `AnalyseProject(projectPath, plcName?, objectName?, severity?, ruleIds?)` | Findings with a rule id, location and suggested name |

`projectPath` is the solution root directory or a `.sln` file inside it. `severity` is the floor for what comes back (`error`, `warning`, `suggestion`); `ruleIds` is a comma-separated allowlist such as `TCK1002`.

Pass `objectName` to check a single POU, GVL or DUT. That is the intended use while writing code: after editing a POU, analyse just that POU and fix what comes back before moving on.

## Rules

| Id | Covers |
|---|---|
| `TCK1001` | POU, DUT and GVL names |
| `TCK1002` | Variable names in any VAR block |
| `TCK1003` | Method, property and action names |
| `TCK1004` | Struct fields and enumeration constants |

Every finding carries the object, the item, the line within that item, the offending identifier, and a suggested name.

Suggestions are advisory. Nothing is rewritten for you, because renaming a symbol that is referenced elsewhere is a change you should agree to rather than discover.

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

The compiler already tells you what does not build. Analysis only earns its place in the gap between "compiles" and "correct", so a rule ships only if `Build` would not have caught it.

That gap is wide in TwinCAT, and naming is the least of it. Rules for the mistakes that compile perfectly and still fail (a function block instance declared in a method, so its state resets every call; `REAL` compared with `=`; a global written from two tasks) are next.
