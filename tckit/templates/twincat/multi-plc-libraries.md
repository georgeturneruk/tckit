# Multi-PLC builds with library references

When a solution contains two or more PLC projects and one
references another as a compiled library, the consumer build
resolves against the **installed** library, not the source on
disk.

## The rule

After editing the library project's source, save and install the
library before rebuilding the consumer.

## With TcKit

```
mcp__tckit__save_plc_as_library(plc_name="MyLib_Plc")
```

This compiles the library project, writes the `.library` artefact,
and installs it into the system repository. After it returns, the
consumer's next build picks up the new code.

## Without TcKit

In the IDE, right-click the library project → **Save as library
and install**. Same effect.

## Why this matters

If you skip the install step and rebuild the consumer, the build
succeeds against the *stale* installed library. Symptoms:

- Tests pass that should fail.
- Tests fail that should pass.
- Library source on disk looks correct, but the binary does not
  match.

Always save+install when crossing the library boundary.
