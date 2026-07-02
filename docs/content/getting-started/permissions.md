# Safety stance

Every mutating tool is gated by `~/.tckit/permissions.json` (or `$TCKIT_HOME/permissions.json`). No file means no restrictions; creating it is how you opt in.

```json
{
  "mode": "write",
  "allowed_net_ids": [],
  "blocked_net_ids": ["192.168.1.50.1.1"]
}
```

## Mode

`mode` is the ceiling on what a session may do:

| Mode | Allows |
|---|---|
| `read` | Inspection only |
| `write` | Also author the project on disk: ST edits, I/O config, builds |
| `execute` | Also act on a live target: `Deploy`, `StartRuntime`, `RunTests`, `WriteSymbols`, `InvokeRpc` |

A tool above the current mode returns an error instead of running.

## Target NetIds

Execute-class tools are additionally gated by target AMS Net ID:

- **`blocked_net_ids`** — targets that can never be acted on. Put production PLCs here. Blocking always wins over the allowlist, and cannot be lifted mid-session: `SetPermissions` can append to this list but never remove from it. Removal means editing the file by hand.
- **`allowed_net_ids`** — when non-empty, execute-class calls are permitted only against these targets. Empty means any non-blocked target.

## Changing it mid-session

The file is hot-reloaded: an edit takes effect on the next tool call, no reconnect. The `GetPermissions` and `SetPermissions` tools read and change the soft facets (`mode`, allowlist, appending a block) from within a session.

## Failure stances

| Situation | Behaviour |
|---|---|
| File missing | Unrestricted (opt-in model) |
| File unparseable | Last good config kept; never silently widens |
| Unknown `mode` value | Falls to `read` |

## Confirmation

Independently of the gate, tools that mutate live PLC state or destroy configuration (`WriteSymbols`, `InvokeRpc`, `DeleteIoDevice`) require `confirmed=true`. The first call without it returns a description of what would happen (for `DeleteIoDevice`, the resolved path and the children that would cascade) and does nothing.
