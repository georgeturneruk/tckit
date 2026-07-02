# CLAUDE.md — TcKit

This file is read automatically by Claude Code at the start of every session.
It holds only the rules that need to fire on every turn. Topic-specific
procedure (writing ST, building, reading projects, editing docs, working with
ADRs, etc.) lives in skills under `.claude/skills/`. Contributor procedure
(adapter/port recipes, code style, local commands, skills workflow, PRs)
lives in [CONTRIBUTING.md](CONTRIBUTING.md). Project tree and port-method
reference live in [docs/content/architecture/overview.md](docs/content/architecture/overview.md).

---

## What this project is

TcKit is an MCP server that connects Claude Code to TwinCAT 3 PLC projects.
Target platform: TwinCAT 3.1 Build 4026.

---

## Architecture: the one rule

**Adapters may only import from ports and stdlib. Never from each other.**

```
MCP Server → Port (abstract) → Adapter (concrete) → External tool
```

The MCP server calls ports. Ports define interfaces. Adapters implement them.
If you need to share logic between adapters, put it in a utility module under
`tckit/utils/` and import that. Never import adapter-to-adapter. This is
enforced by `scripts/check-adapter-isolation.py`, which runs in CI. Do not
break this rule.

Top-level layout (full tree in [architecture/overview.md](docs/content/architecture/overview.md)):

```
tckit/    ← Python package (ports + adapters)
bridge/   ← Windows PowerShell COM bridge
docs/     ← MkDocs site
adrs/     ← Architecture Decision Records
```

---

## Skills (procedural workflows)

Each skill loads on demand on its trigger phrases. The skill body owns the
full procedure; this index just points to it.

| Skill | Loads when |
|-------|------------|
| [tc-orient-project](.claude/skills/tc-orient-project/SKILL.md) | First touch on a TwinCAT project, "structural overview", "what's in this project" |
| [tc-read-project](.claude/skills/tc-read-project/SKILL.md) | Follow-up POU / GVL / DUT lookups once orientation is done |
| [tc-beckhoff-docs](.claude/skills/tc-beckhoff-docs/SKILL.md) | Researching a Beckhoff library FB / function / TF library, or a hardware product by order number |
| [tc-write-st](.claude/skills/tc-write-st/SKILL.md) | Writing or modifying ST code |
| [tc-build-test-loop](.claude/skills/tc-build-test-loop/SKILL.md) | Building, deploying, running TcUnit tests |
| [tc-hardware](.claude/skills/tc-hardware/SKILL.md) | EtherCAT/IPC/axis diagnostics, live symbol I/O, scanning or authoring the I/O tree |
| [tc-config](.claude/skills/tc-config/SKILL.md) | Initial setup, safety stance, permission-gate edits (`permissions.json`), "why was deploy denied" |
| [tc-docs-write](.claude/skills/tc-docs-write/SKILL.md) | Editing anything under `docs/content/` or `README.md` |
| [tc-adr](.claude/skills/tc-adr/SKILL.md) | Reading, writing, or promoting an ADR under `adrs/` |

---

## ADR session-start trigger

Read [`adrs/README.md`](adrs/README.md) at the start of every session. It is
a one-page index with status and a one-line summary per ADR. Then descend
into the **Current state** block (top of the file) of any ADR whose summary
intersects the task. Only read further into an ADR's body when you need
rationale.

The full ADR workflow (layered reading order, status lifecycle, when to
write one, Current state maintenance rule, trim-on-promotion rule,
source-of-truth split, plus the parallel rules for `bench/findings/`)
lives in the [tc-adr skill](.claude/skills/tc-adr/SKILL.md).

---

## Config and secrets

- `~/.tckit/config.toml`: user-global, machine-specific values (AMS IDs, paths,
  safety stance). Scaffold via `tckit init`.
- `tckit/templates/config.toml.example`: committed template, shipped as package
  data and printable via `tckit init --print`.
- `config.json` (project-local, optional): adapter-name overrides only. Never
  put secrets, AMS IDs, or file paths here.
- `docker/.env`: Docker-mode env vars; gitignored. `docker/.env.example` is the
  committed template.

Never commit a `.env` file or any file containing AMS IDs or absolute paths.

---

## Git workflow

Always follow this pattern, no exceptions:

1. Branch from `main`: `git checkout -b feat/my-thing`.
2. Do the work, commit incrementally.
3. Push and open a PR against `main`.
4. Merge via **Squash and merge** on GitHub.
5. Delete the branch after merge.

**Never keep working on a branch after it has been merged.** GitHub's squash
merge gives the commit a new ID, so git will see your old commits as unrelated
to what's on main; any further PR from that branch will show conflicts even
when there are none. One branch, one PR, one squash commit on main.

---

## What NOT to do

- Do not import one adapter from another (mirrors the one rule above).
- Do not hardcode file paths, AMS IDs, or COM version strings.
- Do not fetch full POUs when you need one method (cross-cutting frugality).
- Do not put secrets in `config.json` or commit `.env` files.

Skill-owned guards (deploy gating, safety-name guard, rename guard, comment
style, never-edit-XML-directly, test-loop iteration cap, etc.) live in the
relevant skill and fire when that skill loads. They are not duplicated here.

---

## Contributing

For adapter/port recipes, code style, local commands, the skills workflow,
and the PR workflow, see [CONTRIBUTING.md](CONTRIBUTING.md). For the full
project tree and the port-methods quick reference, see
[docs/content/architecture/overview.md](docs/content/architecture/overview.md).
For docs voice and the "when to update docs" checklist, see the
[tc-docs-write skill](.claude/skills/tc-docs-write/SKILL.md).
