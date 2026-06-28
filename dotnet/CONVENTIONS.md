# TcKit .NET conventions

Naming and formatting follow the official Microsoft C# identifier-names guidance
verbatim
([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/identifier-names)),
enforced by [`.editorconfig`](.editorconfig) + analysers + `dotnet format` in CI.
This file covers only what that guidance does not: the project-specific rules the
rewrite depends on.

## Architecture: the one rule (unchanged from the Python era)

Adapters depend only on `TcKit.Core` (ports + DTOs) and their own external SDK.
Never on each other. Enforced by project references: an adapter project has no
reference to a sibling adapter. `TcKit.Server` is the composition root and may
reference every adapter to register them in DI.

## Ports and DTOs

- Ports are interfaces in `TcKit.Core`, `I`-prefixed, async, with a
  `CancellationToken` on every method.
- Return types are immutable `record` / `readonly record struct` DTOs in
  `TcKit.Core` (the analogue of the Python `ports/types.py` dataclasses).

## Async

ADS and the MCP SDK are async. Async all the way: `...Async` suffix,
`CancellationToken` threaded through, `ConfigureAwait(false)` in Core and adapter
layers. Never `.Result` / `.Wait()` (deadlocks against STA COM).

## COM interop discipline

- All COM lives behind adapters; `TcKit.Core` is COM-free.
- COM calls run on an STA thread, guarded by an `IOleMessageFilter` (lifted from
  TcUnit-Runner).
- Deterministic release: every RCW released in a `finally` / `using` via a
  `ComScope` helper. Never rely on the GC.

## MCP contract

Preserved as-is during the port (tool names, parameter names, response shapes
stay what they are today) so the Python parity oracle can diff byte-for-byte.
Map idiomatic C# methods to the existing names via the SDK attributes. Reshaping
the surface is a deliberate later pass, not part of the port.

## Errors

Domain code throws; the MCP tool layer catches and translates to the existing
structured `{ success, error }` result shape. Never leak a raw `COMException` to
the model.

## Comments and docs

- Why-only comments: explain non-obvious constraints and workarounds, never
  narrate what the code does.
- British English in all prose, comments, and XML-doc. No em dashes.
- XML-doc (`///`) on public ports and tool methods.

## Tests

xUnit, `Method_Scenario_Expectation` naming. The Python golden-master oracle is a
separate integration harness that diffs resulting project artifacts.
