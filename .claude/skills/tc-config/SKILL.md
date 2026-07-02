---
name: tc-config
description: Use when configuring TcKit's safety stance (the permission gate), including initial setup ("set me up", "configure tckit", "make tckit read-only"), ongoing edits ("raise the mode to execute", "add NetId to allowed", "block this NetId"), or inspecting the stance ("what permissions does tckit have", "why was deploy denied", "tckit not working"). Wraps the GetPermissions / SetPermissions MCP tools and the user-global permission file at `~/.tckit/permissions.json`. Do NOT use for runtime tool calls like building, deploying, reading projects, or researching Beckhoff FBs (those are owned by tc-build-test-loop, tc-read-project, tc-write-st, and tc-beckhoff-docs).
allowed-tools: mcp__tckit__GetPermissions, mcp__tckit__SetPermissions, Read, Edit, Write
---

# Configuring TcKit

TcKit's configuration surface is the safety stance: one JSON file at `~/.tckit/permissions.json` (or `$TCKIT_HOME/permissions.json`). The server hot-reloads it on every tool call, so a change (a `SetPermissions` call or a hand edit) takes effect on the next call with no reconnect. The old Python CLI (`tckit init` / `tckit config` / `tckit doctor`) and `~/.tckit/config.toml` are retired; if the user reaches for one of those, drive the flows below instead.

## The two axes

**Mode** is a tier: `read` < `write` < `execute`.

- `read`: inspect only (project reads, doc lookups).
- `write`: also author the project on disk (add/update POUs, GVLs, DUTs).
- `execute`: also act on a live target. Execute-class tools are exactly the NetId-gated set: Deploy, StartRuntime, RunTests, WriteSymbols, InvokeRpc.

A call above the current mode returns `{"error": "Permission denied: ..."}` naming the missing mode.

**Target NetIds** gate execute-class calls by target:

- `blocked_net_ids`: NetIds that can never be acted on from this machine. Block always wins over the allowlist.
- `allowed_net_ids`: if non-empty, execute-class calls are permitted only against these NetIds. Empty means any non-blocked target.

## The two tools

- **`GetPermissions()`**: show the current stance (mode plus both NetId lists). Callable in any mode.
- **`SetPermissions(mode, allowedNetIds, blockNetIds)`**: change the stance and persist it (creates the file on first use). Callable in any mode. Argument semantics:
  - `mode`: `read` | `write` | `execute`; empty leaves it unchanged.
  - `allowedNetIds`: comma-separated list that **replaces** the allowlist; empty leaves it unchanged; the literal `none` clears it (any non-blocked target allowed).
  - `blockNetIds`: comma-separated list **appended** to `blocked_net_ids`. This tool can never remove a blocked NetId; unblocking happens only by editing the file.

## Failure stances (how the gate reads an odd file)

- Missing file: permissive (`execute`, no NetId restrictions). The stance is opt-in.
- Unparseable file: keep the last good settings rather than bricking the server or silently widening.
- Valid file with an unrecognised `mode` value: fall to `read` (a present-but-typo'd mode signals an intent to restrict).
- Valid file with the `mode` key absent: `execute` (unrestricted).

## Flows

### First-time setup ("set me up", "configure tckit")

1. Call `GetPermissions` to see the current stance. A fresh machine reports `execute` with empty lists (no file yet).
2. Ask the user which mode they want and for any allow/block NetIds. Validate every NetId with `^\d+\.\d+\.\d+\.\d+\.\d+\.\d+$` (six dot-separated octets, e.g. `192.168.1.100.1.1`); reject anything else and ask them to retype.
3. Apply with a single `SetPermissions` call; it creates `~/.tckit/permissions.json` and persists.
4. Read back with `GetPermissions` and surface the result.

If the MCP server is not reachable, write the file directly, using the annotated template at `dotnet/permissions.example.json` in the repo as the source (never embed the template content in this skill).

### Change one thing

1. `GetPermissions` to read the current stance.
2. Validate any new NetId format.
3. `SetPermissions` with only the changed argument (the others stay empty, meaning unchanged).
4. State the effect so the user understands it: block always wins; a non-empty allowlist restricts execute-class calls to exactly those targets; the change is live on the next tool call, no reconnect needed.

### Unblock a NetId

`SetPermissions` cannot do this by design. Only on an explicit user request: read `~/.tckit/permissions.json`, confirm the exact NetId with the user, remove it with `Edit`, and remind them that `blocked_net_ids` is the "never touch production" guard, so they know what they just lifted.

### Diagnose ("why was deploy denied", "tckit not working")

1. Call `GetPermissions`. The `Permission denied` text from the failing tool names the missing mode or the offending NetId; cross-check it against the stance.
2. Mode too low: the raise must come from the user. Suggest the `SetPermissions` call but do not make it unprompted.
3. Target in `blocked_net_ids`: hard guard. Surface it and stop; do not offer to lift it unless the user raises it.
4. Target missing from a non-empty `allowed_net_ids`: offer to add it once the user confirms the NetId.
5. If tools fail for reasons other than `Permission denied`, the problem is not this file; hand off to the relevant runtime skill.

## Guardrails

- **Never self-elevate.** A `Permission denied` from another tool is a normal control-flow signal, not an obstacle to route around. Call `SetPermissions` (or edit the file) only when the user explicitly asks for that stance change in chat.
- **Block is hard.** Never remove a `blocked_net_ids` entry on your own initiative, and never suggest deleting the file to get past one.
- **Never loosen by deletion.** A missing file means permissive; deleting or truncating `permissions.json` is a stance change and needs the same explicit user request as any other widening.

## Anti-patterns

- Calling `SetPermissions` to get a denied Deploy or StartRuntime through without the user asking.
- Trying to remove a blocked NetId via `SetPermissions` (append-only by design).
- Telling the user to reconnect via `/mcp` after a stance change; hot-reload makes that unnecessary.
- Driving the retired Python CLI (`tckit init` / `tckit config` / `tckit doctor`) or writing `~/.tckit/config.toml`.
- Embedding the permissions template inline. Point at `dotnet/permissions.example.json`.

## Next

Once the stance is set, normal work uses `tc-read-project`, `tc-beckhoff-docs`, `tc-write-st`, `tc-build-test-loop` as appropriate. This skill stays out of the way until the stance needs changing.
