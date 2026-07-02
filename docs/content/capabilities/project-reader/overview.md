# Reader

Read-only access to TwinCAT project structure and code. The reader parses `.TcPOU`, `.TcGVL`, and `.TcDUT` XML directly from disk; no XAE, no TwinCAT install needed.

Three precision levels; never fetch more than the task needs:

```
GetStructure       → names and types only, no code
GetPouInterface    → declarations + method signatures, no bodies
GetPouItem         → single method/action/property body only
```

| Tool | Returns |
|---|---|
| `GetStructure(projectPath, plcName?)` | Project map: POUs by folder, tasks, libraries, GVL and DUT names |
| `GetPouInterface(pouName, plcName?)` | Declaration + method/property signatures, no bodies |
| `GetPouDeclaration(pouName, plcName?)` | Only the FB-level VAR blocks, no method signatures |
| `GetPouItem(pouName, itemName, plcName?)` | One method, action, or property accessor with its body |
| `GetGvl(gvlName, plcName?)` | Declaration block of a Global Variable List |
| `GetDut(dutName, plcName?)` | Full TYPE definition: struct, enum, union, or alias |

`projectPath` is the solution root directory or a `.sln` file inside it. `GetPouItem` accepts `"Execute"` (method or action), `"Status"` (property header), or `"Status.Get"` / `"Status.Set"` (accessor body).

`GetStructure` builds the per-PLC symbol index the other reads resolve against; call it once at the start of a session.

## Multi-PLC solutions

A solution can hold more than one PLC project (e.g. a library plus a TcUnit test PLC). `GetStructure` keys its result by PLC-project name. The per-symbol tools take an optional `plcName`; without it, a symbol that exists in exactly one PLC auto-resolves, and an ambiguous name returns an error listing the candidate PLC projects.

## Why this shape

Pasting an entire POU file into context to ask about one method is the cheapest way to burn an agent's working memory. Layering reads from structure to interface to single item lets the model pull only the tokens it actually needs; this is [just-in-time context retrieval](https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents).
