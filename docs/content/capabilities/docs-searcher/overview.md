# Docs search

Fetches Beckhoff infosys documentation on demand: function blocks, library pages, and hardware datasheets. Pure HTTP; works anywhere.

| Tool | Purpose |
|---|---|
| `FindFb(fbName)` | Look up a function block or function: inputs, outputs, description |
| `SearchDocs(query, section?)` | Search the cached title indexes across library and hardware sections |
| `FindHardware(orderNumber)` | Look up a hardware product by order number: description plus the parsed "Technical data" table |
| `GetDocPage(url)` | Fetch and parse one infosys page |

Always call `FindFb` before writing code that uses an unfamiliar vendor FB. Vendor FBs have input/output conventions and timing rules that are not reliably in training data.

## How it works

Infosys page URLs are opaque numeric IDs, so the searcher navigates infosys's own menu tree to build per-section title-to-URL indexes. Indexes and fetched pages are cached to disk; the first lookup into a large uncached library section crawls that section (slow, polite delays), and everything after is local.

Covered sections span the TF/TC3 function libraries, the Tc2/Tc3 PLC libraries (system, utilities, motion, fieldbus, building automation, IO-Link), and the hardware families: EtherCAT Terminals (EL/EM/ELM), couplers (EK), EtherCAT Box (EP/EPP/ER/EQ), plug-in modules (EJ), IO-Link boxes (EPI/ERI), and infrastructure (CU).

## FindHardware

`FindHardware("EL3004")` resolves the order number to its doc section, finds the product page, and returns the description plus the technical-data table (channels, resolution, supply, dimensions, ...). Revision-suffixed orders (`EP3174-0002`) resolve to their exact variant.

Known gaps: ER (rugged) and EQ (stainless) boxes share a single generic doc on infosys, so they return the family page with no technical-data table; the AX5000/AX8000 servo drives are not in the doc tree the searcher walks (the motion *programming* libraries are covered via `FindFb`).

## Why this shape

The alternative is pre-loading entire library manuals "just in case", wasting context on pages the agent never needs, or letting the model hallucinate FB signatures from stale training data. Pulling one page at the moment it is needed is the [just-in-time retrieval](https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents) pattern.
