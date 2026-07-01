---
name: tc-config
description: Use when configuring TcKit, including initial setup ("set me up", "configure tckit"), ongoing edits to safety stance ("add NetId to allowed", "edit safety settings", "change confirmation behaviour"), or running health checks ("tckit doctor", "tckit not working", "is the bridge up"). Wraps the `tckit init`, `tckit config`, and `tckit doctor` CLI subcommands and the user-global config file at `~/.tckit/config.toml`. Do NOT use for runtime tool calls like building, deploying, reading projects, or researching Beckhoff FBs (those are owned by tc-build-test-loop, tc-read-project, tc-write-st, and tc-beckhoff-docs).
allowed-tools: Bash, Read, Edit, Write
---

# Configuring TcKit

This skill orchestrates first-time setup and ongoing edits via the `tckit` CLI. It does **not** call MCP tools because config changes happen before the MCP server is fully reachable.

## Subcommands you can drive

- **`tckit init`** — scaffold `~/.tckit/config.toml` from the bundled template (refuses to overwrite without `--force`).
- **`tckit init --print`** — emit the bundled template to stdout. The skill uses this as its single source of truth for the template content.
- **`tckit config show`** — print resolved config and its sources.
- **`tckit config validate`** — check the config for malformed values (returns non-zero on issues).
- **`tckit doctor`** — run health checks: config file present + config validation + bridge reachability. When the bridge is reachable but its launcher isn't installed yet, doctor offers to run `tckit bridge install` for you.
- **`tckit bridge install`** — copy the bundled Windows bridge (`Start-Bridge.ps1` + `harness/`) to `~/.tckit/bridge/`. Refuses to overwrite without `--force`.

## When the user wants to set up TcKit

Triggers: "set me up", "configure tckit", "first time setup".

### Pre-flight

1. Run `tckit config show` to see whether `~/.tckit/config.toml` already exists. If it does, ask before overwriting; offer to `--force` or to edit the existing file in place.
2. Verify `uvx` is on PATH (the plugin's MCP server runs as `uvx tckit`). If missing, tell the user to `pip install uv` first.

This skill targets the stdio path (the plugin and pip installs both use it). Docker mode is CI/dev-only and not driven by this skill; see [docker-setup](../../docs/content/getting-started/docker-setup.md) and issue [#43](https://github.com/georgeturneruk/tckit/issues/43).

### Prompt for values

Ask the user, validating each:

- **TARGET_AMS_ID** — AMS Net ID of their primary PLC or test VM. Validate with regex `^\d+\.\d+\.\d+\.\d+\.\d+\.\d+$` (six dot-separated octets, e.g. `192.168.1.100.1.1`). Reject anything else and ask them to retype.
- **ALLOWED_NETIDS** — optional, comma-separated NetIds. Explain: "These bypass the confirmation gate for Deploy/StartRuntime. Use for test VMs, never production."
- **BLOCKED_NETIDS** — optional. Permanently rejected, cannot be bypassed.
- **SAFETY_CONFIRMATIONS** — default `"true"`. Only set `"false"` if the user explicitly asks; explain that it disables the Deploy/StartRuntime gate entirely.
- **COM_VERSION** — default `"17.0"`. Don't change unless the user knows their TcXaeShell version is different.
- **XAE_MODE** — default `"attach"`. `"headless"` is for CI.
- **BRIDGE_URL** — default `http://localhost:8765`.
- **PLC_PROJECT_NAME** — optional. Name of the PLC sub-project under `TIPC`; set it only to disambiguate a multi-PLC solution (auto-resolved when there's just one). There is no project-path setting: TcKit operates on whatever solution is open in TcXaeShell.

### Write the config

1. Pull the canonical template with `tckit init --print`. **Always** use this as the source — never embed template content in the skill. That keeps the template in one place ([tckit/templates/config.toml.example](../../tckit/templates/config.toml.example)).
2. Substitute the user's values into the template.
3. Write to `~/.tckit/config.toml` (or `$TCKIT_HOME/config.toml`).

If a file already exists and the user said yes to overwrite, prefer `tckit init --force` for the empty scaffold, then edit it. If they said no, just edit in place.

### Verify

Run `tckit doctor` and surface the result. If the bridge is down on first run, doctor will offer to install the bundled bridge to `~/.tckit/bridge/`; accept the prompt (or run `tckit bridge install` manually). Then tell the user to start `~/.tckit/bridge/Start-Bridge.ps1` in a separate PowerShell window with TcXaeShell open, and retry.

### Final prompt

"Done. Open or reload Claude Code, then ask a real question to use TcKit. The Windows bridge (`~/.tckit/bridge/Start-Bridge.ps1`) needs to be running for write/build/deploy/test tools to work; read-only tools work without it."

## When the user wants to change just one thing

### Edit safety stance / add NetId to allowed / block a NetId

1. Read current values via `tckit config show`.
2. Identify the change (typically `ALLOWED_NETIDS` or `BLOCKED_NETIDS`).
3. Validate the new NetId format.
4. Edit `~/.tckit/config.toml` directly.
5. State the precedence rule so the user understands the effect: `BLOCKED > ALLOWED > SAFETY_CONFIRMATIONS > confirmed=True`.
6. Run `tckit doctor` to verify the file parses cleanly.
7. Tell the user to run `/mcp` in Claude Code, then reconnect tckit, so the next MCP call uses fresh env.

## When the user wants to diagnose ("tckit doctor", "tckit not working")

Run `tckit doctor` and read the FAIL lines. Map common failures to next steps:

- **Config file FAIL** ("no config file at ..., TARGET_AMS_ID unset") → run `tckit init`, then edit `~/.tckit/config.toml`.
- **Config FAIL** with NetId issues → drive the edit flow above.
- **Bridge FAIL, launcher not installed yet** → accept doctor's install prompt, or run `tckit bridge install` manually, then start `~/.tckit/bridge/Start-Bridge.ps1` in a PowerShell window with TcXaeShell open.
- **Bridge FAIL, launcher already at `~/.tckit/bridge/`** → tell them to start `~/.tckit/bridge/Start-Bridge.ps1` in a PowerShell window. Verify TcXaeShell is open first.
- **Bridge dependencies FAIL** → re-run `tckit doctor` and accept the install prompt, or `Install-Module -Name <name> -Scope CurrentUser -Force` manually.

## Anti-patterns

- Calling MCP tools (`mcp__tckit__*`) from this skill — config changes happen before MCP is reachable, and config writes don't go through the server.
- Embedding the config template inline. Always pull it with `tckit init --print`.
- Auto-bypassing the safety confirmation gate. If the user wants to skip it for a specific NetId, add to `ALLOWED_NETIDS`. If they want to disable it entirely, ask for explicit confirmation before setting `SAFETY_CONFIRMATIONS=false`.
- Restarting the user's stdio MCP server. You can't — it's spawned by Claude Code per session. Tell the user to use `/mcp` instead.

## Next

Once setup is green, normal work uses `tc-read-project`, `tc-beckhoff-docs`, `tc-write-st`, `tc-build-test-loop` as appropriate. This skill stays out of the way until something needs reconfiguring.
