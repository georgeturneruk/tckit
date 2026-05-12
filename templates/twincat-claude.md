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

## bError propagation

When wrapping another FB, surface its error state:

```pascal
fbInner();
IF fbInner.bError THEN
    bError   := TRUE;
    nErrorId := fbInner.nErrorId;
END_IF
```

Do not silently swallow errors.

## Stop and ask

- **Safety-critical code.** If a name in the change touches `Safety`, `SIL`, `TÜV`/`TUV`, `Emergency`, `EStop`, `SafetyDoor`, or anything similar, stop and ask before any write.
- **Cross-file renames.** The TwinCAT automation interface has no rename API. If a rename touches references in more than one file, stop and ask. Never execute a cross-project rename autonomously.

## Editing project files

If a TwinCAT automation interface (such as TcKit) is available, use it for any structural change. Direct edits to `.TcPOU` and `.plcproj` XML break GUID tracking in ways that may survive a build but corrupt later operations. When in doubt, ask before editing those files by hand.

## Project notes

<!-- Replace with project-specific guidance. -->
