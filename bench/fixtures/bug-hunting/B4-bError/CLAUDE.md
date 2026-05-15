# CLAUDE.md — TwinCAT project conventions

<!--
Drop this at the root of any TwinCAT project. Keep in sync with
the tc-write-st skill in https://github.com/georgeturneruk/tckit
if you have it installed.
-->

Conventions for Structured Text in this project.

## Naming

- POU prefixes: `FB_` function blocks, `PRG_` programs, `GVL_` globals, `E_` enums, `ST_` structs, `I_` interfaces.
- Methods: PascalCase, no prefix.
- Variables: camelCase, no type prefix (e.g. `enableMotor`, `targetSpeed`, `nextState`).
- Match existing style in the file you are editing if it differs.

## Comments

Use RST line comments for method and FB documentation:

```pascal
// :Description:  What this does.
// :param x:      What x is for.
// :returns:      What comes back.
```

Beckhoff XML `(*~ <docu> ~*)` is also accepted. Match the style already present in the file.

## Editing project files

If a TwinCAT automation interface (such as TcKit) is available, use it for any structural change. Direct edits to `.TcPOU` and `.plcproj` XML break GUID tracking in ways that may survive a build but corrupt later operations. When in doubt, ask before editing those files by hand.

## Multi-PLC builds with library references

If this solution contains two or more PLC projects where one references another as a compiled library, the consumer build resolves against the *installed* library. After editing the library project's source, save+install the library (via TcKit's `save_plc_as_library` or the IDE's "PLC project → Save as library and install") before rebuilding the consumer; otherwise the consumer picks up stale code.

## Project notes

<!-- Replace with project-specific guidance. -->
