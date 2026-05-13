# Example: TcUnit

This is the output of TcKit's [html generator](html-generator.md) run against
[TcUnit](https://github.com/tcunit/TcUnit), the open-source unit testing
framework for TwinCAT 3.

[**Browse the generated docs →**](/examples/tcunit/)

The example is rebuilt from the upstream TcUnit `1.3.1` tag on every deploy of
this site, so it always matches the current version of the generator. The
pipeline lives in [`scripts/build-docs.sh`](https://github.com/georgeturneruk/tckit/blob/main/scripts/build-docs.sh)
in the TcKit repo.

## What you are looking at

The generator parses TcUnit's ST source, extracts the comments above each
function block, method, property, and variable, and renders them into a
navigable HTML site with:

- A solution-level index of every PLC project in the solution
- Per-PLC pages for each function block, interface, and DUT
- Field tables for GVLs, structs, and enums with parsed names, types,
  default values, and inline comments
- A type hierarchy view showing `EXTENDS` and `IMPLEMENTS` relationships
- Client-side search across object names, methods, properties, and variables
- Cross-linked type references so clicking a return type or variable type
  jumps to its definition
- Collapsible source views for each method's implementation and the full
  declaration, kept out of the way until you ask for them
- Mobile-friendly responsive layout with a tap-to-open navigation drawer

No TcUnit-specific tweaks. The same pipeline runs against any TwinCAT
solution that has comments in the recognised styles.

## Credit

TcUnit is published under the
[MIT licence](https://github.com/tcunit/TcUnit/blob/master/LICENSE),
Copyright (c) 2018 Jakob Sagatowski and contributors. Used here only as a
real-world example of generator output.
