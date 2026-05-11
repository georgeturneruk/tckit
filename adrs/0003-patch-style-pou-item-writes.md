---
adr: 0003
title: Patch-style writes for fine edits to POU items
status: Exploring
created: 2026-05-10
issue:
pr:
---

## Context

TcKit's read path follows the JIT principle: `get_structure` →
`get_pou_interface` → `get_pou_item` deliver progressively finer
slices, so a "look at one method" question costs ~30 lines of
context.

The write path is coarser. The only fine-grained write today is
`update_pou_item(pou_name, item_name, code)`, which replaces the
**entire body of one method/action/property** (or, by passing
`item_name = pou_name`, the FB-level declaration plus cyclic body —
a behaviour supported by the bridge harness but not currently
documented on the port). There is no smaller unit. A one-line change
to a method body still requires Claude to rewrite the full body and
send it back; adding one VAR_INPUT to an FB declaration requires
rewriting the full declaration block.

Estimated context cost (not measured):

| Operation | Read | Write | Total |
|---|---|---|---|
| Vanilla `Edit` on a Python file (one-line) | ~10 lines | included | ~10 lines |
| TcKit one-line method edit today | ~30 lines | ~30 lines | ~60 lines |
| TcKit add VAR_INPUT to FB today | ~50-150 lines | ~30-60 lines | ~80-200 lines |

So substantive sessions pay roughly 6-10× more context than the
equivalent Python operation, even though the read path is
well-tuned. The bottleneck is the port surface, not the underlying
COM API.

The COM API itself is text-based — `Set-TcItemSource` accepts the
full new source for an item. That is unavoidable on the wire to
XAE. But the **Claude → TcKit** conversation does not have to
mirror it: an adapter can accept a small structured patch, perform
a server-side read-modify-write, and call `Set-TcItemSource` with
the full result. Claude only sends the patch.

## Goals

- Bring fine-edit context cost on TwinCAT close to parity with
  vanilla `Edit` on Python — order-of-magnitude reduction on small
  edits.
- Preserve the no-direct-XML rule. Writes still go through XAE,
  GUID bookkeeping still handled by the IDE.
- Keep the port surface small and additive. Don't replace
  `update_pou_item`; add an alternative for fine edits.
- Avoid fragile coordinate schemes (line numbers / file offsets)
  that break under any whitespace shift or intervening edit.

## Decision (provisional sketch)

### Primary: anchor-based patch on existing items

```python
def update_pou_item_patch(
    self,
    pou_name: str,
    item_name: str,
    old_string: str,
    new_string: str,
) -> Result:
    """Replace one occurrence of old_string with new_string in the
    item's combined declaration + implementation text. Fails if
    old_string is not found, or appears more than once.
    """
```

Mirrors Claude Code's own `Edit` tool. The adapter:

1. Reads the current declaration + implementation (via the
   existing read path or a direct COM call).
2. Finds the unique occurrence of `old_string`.
3. Replaces it.
4. Calls the existing whole-item write to persist.

Passing `item_name = pou_name` keeps the FB-level declaration case
working through the same surface.

### Sibling: structured helper for variable adds

```python
def add_variable(
    self,
    pou_name: str,
    scope: str,            # "VAR_INPUT" | "VAR_OUTPUT" | "VAR" |
                           # "VAR_PERSISTENT" | "VAR_TEMP" | "VAR CONSTANT"
    declaration: str,      # e.g. "bNewParam : BOOL;"
    item_name: str | None = None,  # None = FB-level, else method name
) -> Result:
    """Add a single variable declaration to the named scope block.
    Creates the scope block if it does not exist.
    """
```

Thin convenience over `update_pou_item_patch`: removes the need
for Claude to anchor on `END_VAR` and reason about which scope
block to extend. The most common fine-edit pattern in practice;
worth a dedicated method.

### Companion read shortcut

```python
def get_pou_declaration(self, pou_name: str) -> POUDeclaration:
    """Return only the FB-level declaration block (VAR sections),
    no methods, no signatures, no body.
    """
```

Closes the read-side over-fetch when Claude is preparing to add a
variable. ~10-30 lines vs 50-150 for `get_pou_interface`.
Cheap, narrowly scoped.

## Alternatives considered

- **Line-range patches** (`replace_lines(start, end, text)`).
  Brittle: the read might be from cache or a previous turn, and
  any intervening edit invalidates the range. Anchor-based patches
  survive whitespace and additive changes around them.
- **JSON-Patch / structured AST edits.** Overkill for the
  granularity needed. Would require a parser in the write path;
  today writes are text-only.
- **Many small domain verbs** (`rename_variable`,
  `add_method_param`, `change_return_type`, ...). Each is small,
  but the surface bloats and every one is a new MCP tool. The
  patch primitive plus one helper (`add_variable`) covers the
  80% case; expand only if a specific verb proves repeatedly
  useful.
- **Do nothing, accept the tax.** Defensible if edit-context is
  not a real bottleneck. Argument against: substantive sessions
  today involve dozens of small edits, and a 6-10× context
  multiplier on each compounds quickly. Probably the single
  largest remaining context lever in TcKit.

## Consequences

**Enables:** order-of-magnitude reduction in edit context for
small changes; closer parity with the vanilla `Edit` workflow on
Python; cheaper "modify three methods" sessions.

**Costs:** new port surface (one to three methods); adapter must
implement read-modify-write atomically (or accept that two
clients editing the same item have an interleaving race — the
same risk as today's whole-item writes, just more visible).

**Risks:** anchor uniqueness can fail (the same `old_string`
appears twice in the item). Adapter must fail explicitly with a
useful error so Claude can re-anchor on a longer string. This is
the same failure mode as Claude Code's own `Edit` tool, well
understood.

**Locks out:** nothing. `update_pou_item` stays as the whole-item
write for cases where the patch shape is awkward (large rewrites,
generated bodies).

## Status notes

- 2026-05-10: Drafted as `Exploring`. Validation steps before
  promoting to `Proposed`:
    1. Implement `update_pou_item_patch` against the sample
       fixture and confirm anchor matching survives realistic ST
       formatting (whitespace, comments, CDATA boundaries).
    2. Measure end-to-end context cost on a "modify three
       methods" session with patch vs whole-item replacement;
       confirm the projected ~10× saving.
    3. Decide whether `add_variable` and `get_pou_declaration`
       ship in the same change or follow as separate small PRs.
