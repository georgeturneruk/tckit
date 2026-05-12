# CLAUDE.md — TwinCAT project conventions

<!--
Portable conventions template for TwinCAT 3 projects.

Drop this file at the root of your TwinCAT project as CLAUDE.md so
Claude Code reads it on session start. Add your own project-specific
notes (which FBs matter, how the project is laid out, how to run
things) underneath the conventions, in a "Project notes" section.

Keep in sync with:
  https://github.com/georgeturneruk/tckit  (templates/twincat-claude.md
  and .claude/skills/tc-write-st/SKILL.md carry the same convention
  text; edits should land in both).
-->

This project is a TwinCAT 3 codebase. Follow the conventions below
when reading, writing, or modifying Structured Text. They apply
whether or not the TcKit MCP tooling is available.

---

## Naming conventions

- **POU prefixes.** `FB_` for function blocks, `PRG_` for programs,
  `GVL_` for global variable lists, `E_` for enums, `ST_` for
  structs, `I_` for interfaces.
- **Methods.** PascalCase, no prefix. Example: `Execute`, `GetState`,
  `IsReady`.
- **Variables.** camelCase with type prefix. Match the type's
  prefix character:
  - `b` BOOL
  - `n` INT / UINT / DINT / UDINT / LINT / ULINT
  - `f` REAL / LREAL
  - `s` STRING / WSTRING
  - `e` ENUM
  - `st` STRUCT
  - `a` ARRAY
  - `p` POINTER
  - `i` interface

Examples: `bEnable`, `nCount`, `fSetpoint`, `sName`, `eState`,
`stConfig`, `aBuffer`, `pTarget`, `iCallback`.

---

## Comment style

Prefer RST line comments for new code:

```pascal
// :Description:  Holds the rolling average of the last N samples.
// :param nNewSample:  The sample to add to the window.
// :returns:  The current average across the window.
```

Beckhoff XML `(*~ <docu> ~*)` is also accepted. Match the style
already present in the file you are editing; do not mix the two
within a single POU.

Avoid plain `// comments` for documentation. They are fine for
inline notes inside a method body. RST line comments are the
format the doc generator and downstream tooling expect for
method-level and FB-level docs.

---

## Error propagation pattern

Function blocks that wrap other FBs propagate `.bError` and
`.nErrorId` up the call chain:

```pascal
fbInner();
IF fbInner.bError THEN
    bError   := TRUE;
    nErrorId := fbInner.nErrorId;
    eState   := E_State.Error;
END_IF
```

Do not swallow errors silently. If an inner FB sets `bError`, the
outer FB must either surface it or document explicitly why it is
recovering. The default rule is: propagate.

---

## Safety-name guard

If any name in a change touches `Safety`, `SIL`, `TÜV` / `TUV`,
`Emergency`, `EStop`, `SafetyDoor`, or anything else that
suggests safety-critical functionality, **stop**. Show the
proposed change, explain that it appears safety-critical, and
ask for explicit approval before any write. This applies even
if the change seems trivial.

Safety-critical code follows a different review process than
ordinary application code. Defer to the human reviewer.

---

## Rename guard

Renames are blast-radius operations. The TwinCAT automation
interface does not offer a rename API; cross-project renames
require manual find-and-replace, and a mistake silently breaks
references.

If a change involves renaming a symbol (FB, method, variable,
type) that exists in more than one file, **stop**. Report how
many references exist and ask for approval before any rename.
Never execute a cross-project rename autonomously.

---

## Editing project files directly

If a TwinCAT automation interface (such as TcKit) is available,
use it for any structural change: adding methods, POUs, or
variables; modifying GUIDs; updating `<Compile Include="..."/>`
entries in `.plcproj`.

If no automation interface is available, **be cautious**.
`.TcPOU` and `.plcproj` files are XML with embedded CDATA blocks
and GUIDs that TwinCAT tracks. A direct edit that breaks GUID
uniqueness or `.plcproj` cross-references can corrupt the
project in ways that survive a build but break later operations
(library import, source-control merges, XAE refresh). Edit
directly only with full understanding of the implications.

When in doubt: ask before editing `.TcPOU` or `.plcproj` by hand.

---

## TcUnit results

If this project runs TcUnit tests and a tooling layer (such as
TcKit) reads the results, set the export path explicitly:

```pascal
VAR_GLOBAL CONSTANT
    TcUnit_ResultExportXmlPath : T_MaxString :=
        'C:\TwinCAT\3.1\Boot\Plc\TcUnitResults.xml';
END_VAR
```

The exact path can vary per deployment; the convention is to
pin it deterministically so the consumer knows where to read.
See ADR-0006 in the TcKit repo for details if relevant.

---

## TcKit (optional)

[TcKit](https://github.com/georgeturneruk/tckit) is an MCP server
that exposes TwinCAT project operations as Claude Code tools.
It enforces the conventions above through its writer tools (which
route structural changes through XAE) and the `tc-write-st`
skill (which carries the same convention text as this file).

Install if you want Claude to handle method/POU/variable adds
without hand-editing XML. Not required for the conventions in
this file.

---

## Project notes

<!--
Replace this section with your own project-specific guidance:
which FBs are central, how the codebase is laid out, where to
run things, which conventions deviate from the defaults above.

Examples:

- The code under test lives in `Library/`. Tests live in
  `Tests/`. Tests are a linked-library reference to Library.
- The cyclic task runs at 1 ms; long-running operations belong
  in an asynchronous task.
- Deviation from the default: this codebase uses `m_` (not
  camelCase) for private member variables on FBs, for historical
  reasons. Match the surrounding code.
-->
