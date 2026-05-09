# CLAUDE.md — TcKit

This file is read automatically by Claude Code at the start of every session.
It contains the orientation Claude needs to work on the TcKit codebase. The
*procedural* rules (how to read, write, build, test, add adapters) live in
skills under `.claude/skills/` — see the index below.

---

## What This Project Is

TcKit is an MCP server that connects Claude Code to TwinCAT 3 PLC projects.
It allows Claude to read project structure, write ST code, trigger builds,
deploy to external targets, run TcUnit tests, and iterate autonomously.

Target platform: TwinCAT 3.1 Build 4026.
Python package lives in `tckit/`. Bridge service (Windows) lives in `bridge/`.

---

## Architecture — The One Rule

**Adapters may only import from ports and stdlib. Never from each other.**

This is enforced by `scripts/check-adapter-isolation.py`, which runs in CI.
Do not break this rule under any circumstances.

```
MCP Server → Port (abstract) → Adapter (concrete) → External tool
```

The MCP server calls ports. Ports define interfaces. Adapters implement them.
If you need to share logic between adapters, put it in a utility module under
`tckit/utils/` and import that — never import adapter-to-adapter.

---

## Project Structure

```
tckit/
├── tckit/
│   ├── server.py           ← MCP server entry point
│   ├── config.py           ← config + .env loader
│   ├── ports/              ← abstract base classes ONLY
│   │   ├── reader.py
│   │   ├── writer.py
│   │   ├── builder.py
│   │   ├── test_runner.py
│   │   ├── doc_generator.py
│   │   └── docs_searcher.py
│   └── adapters/           ← concrete implementations
│       ├── readers/
│       ├── writers/
│       ├── builders/
│       ├── test_runners/
│       ├── doc_generators/
│       └── docs_searchers/
├── bridge/                 ← PowerShell, Windows only
├── tests/
│   └── fixtures/sample_project/   ← real .TcPOU files for testing
├── docs/                   ← MkDocs website
└── docker/
```

---

## Skills (Procedural Workflows)

Each procedure for working with TcKit lives in a skill that loads on demand.
See the linked SKILL.md for the full step-by-step.

| Skill | When it loads | What it owns |
|-------|---------------|--------------|
| [`tc-read-project`](.claude/skills/tc-read-project/SKILL.md) | Inspecting / navigating / searching a TwinCAT project | Layered read pattern (`get_structure → get_pou_interface → get_pou_item`), `get_dut` and `get_gvl` use |
| [`tc-beckhoff-docs`](.claude/skills/tc-beckhoff-docs/SKILL.md) | Researching a Beckhoff library FB / function / TF library | `find_fb` precondition for unfamiliar FBs, `search_docs` / `get_doc_page` etiquette, source-URL citations |
| [`tc-write-st`](.claude/skills/tc-write-st/SKILL.md) | Writing or modifying ST code | Comment style (RST line preferred; Beckhoff XML accepted), naming, `bError` propagation pattern, rename guard, safety-name guard, "never edit XML directly" |
| [`tc-build-test-loop`](.claude/skills/tc-build-test-loop/SKILL.md) | Building, deploying, running TcUnit tests | Build-before-deploy, 2-attempt build-fix cap, 5-iteration test cap, `awaiting_confirmation` handshake for deploy/start_runtime, tolerating `docs_warning` on a successful build |

---

## Design Decisions (ADRs)

Non-trivial design decisions live as Architecture Decision Records under
`adrs/`. One Markdown file per decision, with frontmatter status
(`Exploring | Proposed | Accepted | Implemented | Superseded`). Template
at `adrs/0000-template.md`.

`Exploring` precedes `Proposed` and is for ADRs that capture investigation
before a specific proposal is on the table. Use it when the work is
"here are the options we've evaluated and what we know about each", not
"here is what we propose to do". Promote to `Proposed` once a recommended
direction crystallises.

**When to write one.** Only if you would lose meaningful context by not
writing it down before stopping. Single-session work doesn't qualify.
Routine bug fixes don't qualify. ADRs are for design choices that span
multiple sessions or that future-you will need to understand the *why* of.

**At session start.** If `adrs/` contains any ADR with `status: Exploring`,
`status: Proposed` or `status: Accepted` relevant to the user's request,
read those before doing work and propose an orientation: which ADRs
intersect with the request, and a suggested sequence for this session. Do
not write the session orientation to disk; it is verbal context.

**Decisions flow back into the ADR.** When a session lands on something
that contradicts or extends an ADR (tried X, switched to Y), update its
Status notes section before moving on. This is the rule that keeps ADRs
honest.

**Mark Implemented when the PR lands.** Set `status: Implemented` and fill
in `pr:`. If an ADR is abandoned, set `status: Superseded` with a note
explaining why; do not delete it.

**Source-of-truth split:**

- `adrs/*.md`: design rationale (how something should work, why this approach)
- GitHub issues / PRs: tracked work (what is open, what shipped)
- Code: implemented behaviour (the truth on what currently exists)
- `CLAUDE.md`: cross-session rules and conventions

If two artefacts try to own the same thing, one is wrong.

---

## Adding a New Adapter

Adapters live under `tckit/adapters/<port_kind>/`. The one hard rule: adapters import only from `tckit.ports` and stdlib. Never from another adapter (linting enforces this).

1. Create the file in the correct `adapters/` subdirectory
2. Import only from `tckit.ports` and stdlib
3. Implement all abstract methods from the port
4. Register it in `tckit/config.py` adapter registry
5. Add the config name to `config.example.json`
6. Write unit tests in `tests/unit/`
7. Document it in `docs/content/capabilities/<port>/`

---

## Adding a New Port

Adding a port is rare. Only do this if there is a genuinely new external
concern — not a variation of an existing one. Discuss with the user before
adding a port.

1. Define the abstract base class in `tckit/ports/`
2. Keep method signatures minimal — only what adapters need to implement
3. Return types should be dataclasses defined in `tckit/ports/types.py`
4. Update `tckit/server.py` to expose the new port as MCP tools
5. Implement at least one adapter before merging

---

## Running the Project Locally

```bash
# Start MCP server
docker compose -f docker/docker-compose.yml up

# Run tests
docker compose -f docker/docker-compose.yml run tckit pytest tests/

# Lint
docker compose -f docker/docker-compose.yml run tckit ruff check tckit/

# Adapter isolation check (stdlib only — runs outside Docker)
python scripts/check-adapter-isolation.py
```

Windows bridge (run natively on Windows PC):
```powershell
.\bridge\Start-Bridge.ps1
```

---

## Config & Secrets

- `config.json` — committed, no secrets, adapter names and non-sensitive settings
- `.env` — gitignored, machine-specific values (paths, IPs, AMS IDs)
- `.env.example` — committed template

Never put secrets, AMS IDs, or file paths in `config.json`.
Never commit any `.env` file.

---

## Git Workflow

Always follow this pattern — no exceptions:

1. Branch from `main`: `git checkout -b feat/my-thing`
2. Do the work, commit incrementally
3. Push and open a PR against `main`
4. Merge via **Squash and merge** on GitHub
5. Delete the branch after merge

**Never keep working on a branch after it has been merged.** GitHub's squash merge gives the commit a new ID, so git will see your old commits as unrelated to what's on main — any further PR from that branch will show conflicts even when there are none.

**One branch, one PR, one squash commit on main.**

---

## What NOT to Do

- Do not import one adapter from another
- Do not hardcode file paths, AMS IDs, or COM version strings
- Do not fetch full POUs when you need one method
- Do not skip `find_fb()` when using an unfamiliar Beckhoff FB
- Do not make structural project changes via direct XML editing
- Do not execute cross-project renames autonomously
- Do not modify safety-critical code without human review
- Do not deploy without a successful build
- Do not auto-confirm a deploy or `start_runtime` call — surface `awaiting_confirmation` to the user and wait for explicit go-ahead
- Do not continue looping after 5 failed test iterations
- Do not put secrets in `config.json` or commit `.env` files
- Do not write code comments in a format other than RST line (`// :Description:`) or Beckhoff XML (`(*~ <docu> ~*)`)

---

## Quick Reference — Port Methods

| Port | Key Methods |
|------|------------|
| ProjectReader | `get_structure()`, `get_pou_interface()`, `get_pou_item()`, `get_gvl()`, `get_dut()` |
| ProjectWriter | `open_project()`, `create_project()`, `add_pou()`, `add_method()`, `update_pou_item()` |
| BuildRunner | `build()`, `deploy()`, `start_runtime()`, `get_status()` |
| TestRunner | `run_tests()`, `wait_complete()`, `get_results()`, `get_status()` |
| DocGenerator | `generate()`, `get_status()` |
| DocsSearcher | `find_fb()`, `find_library()`, `search()`, `get_page()` |
