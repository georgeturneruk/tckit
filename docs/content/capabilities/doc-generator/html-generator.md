# html generator

**Port:** `DocGenerator`  
**Module:** `tckit.adapters.doc_generators.html_generator.HtmlGenerator`  
**Config key:** `"html"` (default)  
**Status:** Phase 1 — complete

Generates self-contained HTML documentation from comments embedded in TwinCAT ST source. No Sphinx, no plcdoc, no external dependencies beyond Jinja2.

A `MarkdownGenerator` adapter (`"markdown"`) is also available for GitHub wikis, Confluence, and Obsidian.

## Configuration

```json
{
  "doc_generator": "html",
  "docs_output_path": "./docs/plc"
}
```

Set `"doc_generator": "markdown"` to output `.md` files instead.

## Comment styles — auto-detected

Three comment styles are detected automatically. No configuration required.

**RST line comments** (recommended for new code):
```pascal
// :Description: Brief description of what this FB does
// :param bEnable: Rising edge triggers the operation
// :param sNetId: AMS Net ID of the target device
// :returns: TRUE when operation completes successfully
METHOD Execute : BOOL
```

**Beckhoff XML `<docu>` comments** (TcOpen / TE1030 style):
```pascal
(*~
<docu>
    <summary>Brief description</summary>
    <param name="bEnable">Rising edge triggers the operation</param>
    <returns>TRUE when done</returns>
</docu>
~*)
METHOD Execute : BOOL
```

**RST block comments** (plcdoc convention):
```pascal
(* :Description: Brief description
:param bEnable: Rising edge triggers the operation
*)
METHOD Execute : BOOL
```

## Output

For each project the generator produces:

| File | Contents |
|------|----------|
| `index.html` | Project overview with type-grouped object table |
| `<Name>.html` | Per-object page: variables, methods, properties, used-by |
| `hierarchy.html` | Inheritance tree and interface implementation groups |
| `search-index.json` | lunr.js search index for client-side search |

Features: cross-reference links between types, dark/light theme toggle, client-side search, "Used by" back-references, "Built with TcKit" footer.

## MCP tools

```
generate_docs(project_path, output_path)  → Result
get_doc_status()                          → {status: idle|generating|complete|error}
```

## Running standalone

```bash
# Generate from sample fixtures, serve on :8080
docker compose -f docker/docker-compose.docs.yml up --build

# Generate from a real project
DOCS_PLC_PATH=C:/MyProject/PLC \
  docker compose -f docker/docker-compose.docs.yml up
```
