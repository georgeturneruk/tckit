# Doc generator

Renders project documentation directly from comments embedded in the ST source. Reads the local `.TcPOU` / `.TcGVL` / `.TcDUT` tree; no XAE, no TwinCAT install needed.

| Tool | Purpose |
|---|---|
| `GenerateDocs(projectPath, outputPath, format?)` | `format` is `html` (self-contained site, default) or `markdown` (GitHub Flavoured Markdown) |
| `GetDocStatus()` | Status of the most recent run: idle, generating, complete, or error |

## Comment styles

Three styles, auto-detected per item; no configuration.

**RST line comments** (recommended for new code):

```pascal
// :Description: Brief description of what this FB does
// :param bEnable: Rising edge triggers the operation
// :returns: TRUE when operation completes successfully
METHOD Execute : BOOL
```

**Beckhoff XML `<docu>`** (TcOpen / TE1030 style) and **RST block comments** (plcdoc convention) are also recognised.

## Output

| Format | Files |
|---|---|
| `html` | `index.html`, per-object pages, `hierarchy.html` (EXTENDS / IMPLEMENTS tree), client-side search, dark/light theme |
| `markdown` | `index.md` plus per-object pages with cross-linked types |

Multi-PLC solutions get a solution-level index and a sub-tree per PLC project. See the [TcUnit example](tcunit-example.md) for real output.

## Why this shape

Wikis drift. Code does not, at least not from itself. Generating docs from comments next to the code they describe means one source of truth for "what does this FB do"; an agent reading either the source or the generated docs gets the same answer.
