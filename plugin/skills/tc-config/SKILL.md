---
name: tc-config
description: Use when configuring TcKit, including initial setup ("set me up", "configure tckit"), ongoing edits to safety stance ("add NetId to allowed", "edit safety settings", "change confirmation behaviour"), runtime mode switches ("switch to docker", "switch to stdio"), or running health checks ("tckit doctor", "tckit not working", "is the bridge up"). Wraps the `tckit config` and `tckit doctor` CLI subcommands and the user-global config file at `~/.tckit/config.toml`. Do NOT use for runtime tool calls like building, deploying, reading projects, or researching Beckhoff FBs (those are owned by tc-build-test-loop, tc-read-project, tc-write-st, and tc-beckhoff-docs).
allowed-tools: Bash, Read, Edit, Write
---

# Configuring TcKit

This skill orchestrates first-time setup and ongoing edits via the `tckit` CLI. It does **not** call MCP tools because config changes happen before the MCP server is fully reachable.

## Subcommands you can drive

- **`init`** — first-time setup walkthrough (this skill, no CLI subcommand).
- **`tckit config show`** — print resolved config and its sources.
- **`tckit config validate`** — check the config for malformed values (returns non-zero on issues).
- **`tckit doctor`** — run health checks: config validation + bridge reachability.

## When the user wants to set up TcKit (`init` flow)

Use this when they say "set me up", "configure tckit", or "first time setup".

### Pre-flight detection

1. Check whether `~/.tckit/config.toml` already exists. If so, ask the user before overwriting; offer `tckit config show` so they can see the current state.
2. Detect runtime mode:
   - **stdio** (default, plugin install): the user is most likely on this path. The Python package is installed via uvx/pipx and `~/.tckit/config.toml` is the canonical place for settings.
   - **docker** (opt-in): only available when the user has cloned the repo. Detect by looking for `docker/docker-compose.yml` in the current directory tree. If absent, refuse Docker mode with: "Docker mode needs the cloned repo for `docker/.env` and the compose file. Use stdio mode instead, or `git clone https://github.com/georgeturneruk/tckit` and try again."
3. Check for `uvx` (stdio mode) or `docker` (Docker mode) on PATH. If missing, surface a clear error before going further.

### Prompts (both modes)

Ask the user, validating each:

- **TARGET_AMS_ID** — the AMS Net ID of their primary PLC or test VM. Validate with regex `^\d+\.\d+\.\d+\.\d+\.\d+\.\d+$` (six dot-separated octets, e.g. `192.168.1.100.1.1`). Reject anything else and ask them to retype.
- **ALLOWED_NETIDS** — optional. If set, must be comma-separated NetIds matching the same regex. Explain: "These bypass the confirmation gate for deploy/start_runtime. Use for test VMs, never production."
- **SAFETY_CONFIRMATIONS** — default `"true"`. Only set `"false"` if the user explicitly asks; explain that it disables the deploy/start_runtime gate entirely.
- **BLOCKED_NETIDS** — optional. Permanently rejected, cannot be bypassed.
- **COM_VERSION** — default `"17.0"`. Don't change unless the user knows their TcXaeShell version is different.
- **XAE_MODE** — default `"attach"`. `"headless"` is for CI.
- **PLC_PROJECT_PATH** — optional convenience. The absolute path to a `.sln` they mostly work with.

### Mode-specific extras

**stdio mode:** ask for **BRIDGE_URL** (default `http://localhost:8765`).

**Docker mode:** also ask for:
- **PLC_PROJECTS_HOST_PATH** — host directory containing PLC projects, mounted into the container at `/projects`. Default `./projects` (relative to repo). Validate that the path exists.
- **BRIDGE_URL** — note: in Docker mode the default is `http://host.docker.internal:8765` (the container reaches the host's bridge through Docker's DNS).

### Writing config

**stdio mode:**
1. Create `~/.tckit/` if missing (or `$TCKIT_HOME` if set).
2. Write `~/.tckit/config.toml` using the template at `${CLAUDE_PLUGIN_ROOT}/templates/config.toml.example` or the project's `.claude/skills/tc-config/templates/config.toml.example`.
3. Substitute the user's values into the template.

**Docker mode:**
1. Copy `docker/.env.example` to `docker/.env` and `.env.example` to `.env` if they don't exist.
2. Edit each file, replacing `KEY=` lines with the user-supplied values.
3. Print the manual MCP registration line (the plugin only auto-registers stdio):
   ```
   claude mcp add --transport sse tckit http://localhost:8000/sse
   ```

### Verification

After writing config, run `tckit doctor` and surface the result. If the bridge is down (typical for first-run before `Start-Bridge.ps1` has been launched), tell the user to start the bridge in a separate PowerShell window and retry.

### Final step

Print the next-action prompt the user needs:

- **stdio mode**: "Done. Open or reload Claude Code, then ask a real question to use TcKit."
- **Docker mode**: "Done. Run `docker compose -f docker/docker-compose.yml up --build -d` and then `claude mcp add --transport sse tckit http://localhost:8000/sse`."

In both cases, also remind them: **the Windows bridge needs to be running** for write/build/deploy/test tools to work. Start it with `.\bridge\Start-Bridge.ps1` in PowerShell, with TcXaeShell already open.

## When the user wants to change just one thing

### "Edit safety stance" / "add NetId to allowed" / "block this NetId"

1. Read current values via `tckit config show`.
2. Identify the change (typically `ALLOWED_NETIDS` or `BLOCKED_NETIDS`).
3. Validate the new NetId format.
4. Update either `~/.tckit/config.toml` (stdio mode) or `docker/.env` (Docker mode).
5. State the precedence rule explicitly so the user understands the effect: `BLOCKED > ALLOWED > SAFETY_CONFIRMATIONS > confirmed=True`.
6. Run `tckit doctor` to verify the file parses cleanly.
7. Tell the user how to apply the change:
   - **stdio**: `/mcp` in Claude Code, then reconnect tckit. The next MCP call uses fresh env.
   - **Docker**: `docker compose restart tckit` (run it for them via Bash if they say to).

### "Switch to docker" / "switch to stdio"

1. Confirm the user really wants to switch (it's a non-trivial change).
2. Read existing config from current mode's location.
3. Write equivalent values to the other mode's location, preserving `TARGET_AMS_ID`, `ALLOWED_NETIDS`, `BLOCKED_NETIDS`, `SAFETY_CONFIRMATIONS`, `COM_VERSION`, `XAE_MODE`.
4. Adjust mode-specific defaults (`BRIDGE_URL` flips between `localhost:8765` and `host.docker.internal:8765`).
5. Tell the user how to bring up the new mode.

## When the user wants to diagnose ("tckit doctor", "tckit not working")

Just run `tckit doctor` and show the output. Read the FAIL lines and offer concrete next steps:

- **Config FAIL** with NetId issues → drive the "edit" flow above.
- **Bridge FAIL** → tell them to start the bridge with `.\bridge\Start-Bridge.ps1`. Verify TcXaeShell is open first.

## Anti-patterns

- Calling MCP tools (`mcp__tckit__*`) from this skill — config changes happen before MCP is reachable, and config writes don't go through the server.
- Auto-bypassing the safety confirmation gate. If the user wants to skip it for a specific NetId, add to `ALLOWED_NETIDS`. If they want to disable it entirely, ask for explicit confirmation before setting `SAFETY_CONFIRMATIONS=false`.
- Editing `~/.tckit/config.toml` while the user is in Docker mode (or vice versa). Stdio mode reads the TOML; Docker mode reads `.env`. The wrong file is silent dead weight.
- Restarting the user's stdio MCP server. You can't — it's spawned by Claude Code per session. Tell the user to use `/mcp` instead.

## Next

Once setup is green, normal work uses `tc-read-project`, `tc-beckhoff-docs`, `tc-write-st`, `tc-build-test-loop` as appropriate. This skill stays out of the way until something needs reconfiguring.
