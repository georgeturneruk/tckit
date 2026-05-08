# markdown generator

**Port:** `DocGenerator`  
**Module:** `tckit.adapters.doc_generators.markdown_generator.MarkdownGenerator`  
**Config key:** `"markdown"`  
**Status:** Phase 1 — complete

Generates GitHub Flavoured Markdown documentation from comments embedded in TwinCAT ST source. Same parser and project model as [`html_generator`](html-generator.md) — different renderer.

Useful for GitHub wikis, Confluence imports, Notion, Obsidian, and any Markdown-based documentation system.

## Configuration

```json
{
  "doc_generator": "markdown",
  "docs_output_path": "./docs/plc"
}
```

## Comment styles

Same auto-detected styles as the HTML generator: RST line, RST block, and Beckhoff XML `<docu>`. See [`html_generator`](html-generator.md#comment-styles--auto-detected) for examples.

## Output

For each project the generator produces:

| File | Contents |
|------|----------|
| `index.md` | Project overview with type-grouped object table |
| `<Name>.md` | Per-object page: variables, methods, properties, used-by |

Markdown features used: pipe tables, fenced code blocks, internal `[Name](Name.md)` links between types.

The HTML generator's hierarchy page and client-side search index are HTML-only — they do not have Markdown equivalents.

## MCP tools

```
generate_docs(project_path, output_path)  → Result
get_doc_status()                          → {status: idle|generating|complete|error}
```

The same tools serve both generators. Behaviour is selected by the `"doc_generator"` config key.
