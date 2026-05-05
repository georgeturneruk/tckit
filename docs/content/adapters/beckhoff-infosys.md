# beckhoff infosys

**Port:** `DocsSearcher`  
**Module:** `tckit.adapters.docs_searchers.beckhoff_infosys.BeckhoffInfosys`  
**Status:** Phase 1 — in progress

Searches and retrieves Beckhoff infosys documentation using httpx + BeautifulSoup. Pages are cached locally to protect against HTML structure changes.

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

## Infosys structure

```
https://infosys.beckhoff.com/content/1033/
  tc3_plc_intro/       ← PLC programming guide
  tcplclib_tc2_*/      ← TC2 standard libraries
  tcplclib_tc3_*/      ← TC3 libraries
  tf6xxx_*/            ← TwinCAT functions
  tc3_ads_intro/       ← ADS documentation
```
