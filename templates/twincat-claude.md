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

## TcUnit test projects

If this project hosts TcUnit suites that TcKit's runner will execute, declare the XML output path as a `VAR_GLOBAL CONSTANT` so the runner can resolve it without env config:

```pascal
VAR_GLOBAL CONSTANT
    TcUnit_ResultExportXmlPath : T_MaxString :=
        'C:\TwinCAT\3.1\Boot\Plc\TcUnitResults.xml';
END_VAR
```

The path is read from the declaration text at run time (compile-time constant lookup), so it's robust across runtime states. The canonical path above works for most setups; override the literal if you need somewhere else.

## Project notes

<!-- Replace with project-specific guidance. -->
