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

Byte-for-byte parity with the Python tools is **not** a goal; the port is free to
make deliberate, reviewed improvements to the surface. The Python stack is a
behavioural reference and the oracle a semantic cross-check (see `oracle/`), not
a strict diff. The per-tool verification gate is the xUnit suite.

The MCP surface follows our C# identifier conventions rather than the snake_case
ecosystem default: **PascalCase tool names** (set explicitly via
`[McpServerTool(Name = "...")]`, since the SDK would otherwise camelCase the
method name) and **camelCase parameters** (the C# parameter convention; the SDK
uses the parameter name verbatim). Output JSON keys are a separate data contract
and stay snake_case via the shared `TckitJson` options.

## Errors and result shape

Domain code throws; the MCP tool layer catches and translates. The unified
convention across tools: **success returns the tool's data object** (serialised
via the shared `TckitJson` options: snake_case names, string enums, nulls
emitted); **failure returns `{ "error": "<message>" }`**. Never leak a raw
`COMException` to the model.

## Comments and docs

- Why-only comments: explain non-obvious constraints and workarounds, never
  narrate what the code does.
- British English in all prose, comments, and XML-doc. No em dashes.
- XML-doc (`///`) on public ports and tool methods.

## Tests

xUnit, `Method_Scenario_Expectation` naming. The Python golden-master oracle is a
separate integration harness that diffs resulting project artifacts.
