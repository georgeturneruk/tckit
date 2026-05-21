# CLAUDE.md — TwinCAT conventions

Conventions for Structured Text in this project. Copied here by
`tckit init --with-claude-md`; tckit will not overwrite it. Edit
freely; treat the files under `twincat/` as the deep dive for each
rule.

## Conventions

- **Cyclic logic in a method, not the FB body.** The FB's implicit
  body is not part of any `INTERFACE` contract — consumers holding
  `I_Foo` cannot reach code written there. → see
  [twincat/cyclic-in-method.md](twincat/cyclic-in-method.md).
- **Interfaces for FB collections.** Arrays of interface references
  are the safe way to loop over heterogeneous FBs; pointers are
  unsafe and `REFERENCE TO` cannot be arrayed. → see
  [twincat/polymorphism-arrays.md](twincat/polymorphism-arrays.md).
- **TcUnit tests are ordered and self-named.** Use `TEST_ORDERED`
  instead of `TEST`, and name with `__POUNAME()` so test names
  track method renames. → see
  [twincat/tcunit-tests.md](twincat/tcunit-tests.md).
- **POU and variable naming.** Prefixes for POU kinds, PascalCase
  methods, camelCase variables. → see
  [twincat/naming.md](twincat/naming.md).
- **Comment style.** RST line comments or Beckhoff XML, matching
  the file's existing style. → see
  [twincat/comments.md](twincat/comments.md).
- **Multi-PLC builds.** Save+install the library before rebuilding
  the consumer. → see
  [twincat/multi-plc-libraries.md](twincat/multi-plc-libraries.md).

## Editing project files

If a TwinCAT automation interface (such as TcKit) is available, use
it for any structural change. Direct edits to `.TcPOU` and
`.plcproj` XML break GUID tracking in ways that may survive a build
but corrupt later operations. When in doubt, ask before editing
those files by hand.

## Project notes

<!-- Replace with project-specific guidance. -->
