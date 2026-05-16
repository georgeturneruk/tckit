# beckhoff infosys

**Port:** `DocsSearcher`  
**Module:** `tckit.adapters.docs_searchers.beckhoff_infosys_searcher.BeckhoffInfosysSearcher`  
**Status:** Phase 1 — complete

Searches and retrieves Beckhoff infosys documentation. Uses DuckDuckGo HTML search to resolve infosys page URLs (which use opaque numeric IDs), then fetches and parses the content directly. Pages are cached locally — each FB is only looked up once.

## Configuration

```json
{
  "docs_searcher": "beckhoff_infosys",
  "infosys_cache_path": "./cache/infosys",
  "infosys_lang": "1033"
}
```

`infosys_lang`: `1033` = English (default), `1031` = German.

## Usage

Always call `find_fb()` before writing code that uses an unfamiliar Beckhoff FB:

```
find_fb("FB_EcCoESdoRead")
→ returns inputs, outputs, timing notes, description
```

The first call searches DuckDuckGo to resolve the correct URL and fetches the page. Subsequent calls for the same FB return instantly from cache.

## How URL resolution works

Beckhoff infosys uses opaque numeric IDs for individual pages (e.g. `84136843.html`). Rather than maintaining a curated index, the adapter uses DuckDuckGo's HTML search endpoint with a `site:infosys.beckhoff.com` query to find the correct URL automatically.

```
find_fb("FB_ClientConnect")
  → DDG: site:infosys.beckhoff.com FB_ClientConnect
  → https://infosys.beckhoff.com/content/1033/tf6310_tc3_tcpip/84136843.html
  → fetch + parse → FBDoc(inputs=[...], outputs=[...])
```

## MCP tools

```
find_fb(fb_name)              → FBDoc
search_docs(query, section?)  → SearchResults
get_doc_page(url)             → DocPage
```

`get_doc_page` accepts both direct content URLs and the `english.php?content=...` wrapper format — both are handled transparently.
