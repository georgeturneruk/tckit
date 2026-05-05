# Ports

Each port is a Python abstract base class in `tckit/ports/`. The MCP server calls ports. Adapters implement them.

## ProjectReader

**File:** `tckit/ports/reader.py`  
**Purpose:** Read-only access to TwinCAT project structure and code.

Always use the layered approach:

```
get_structure()      → names and types only, no code
get_pou_interface()  → declarations + method signatures, no bodies
get_pou_item()       → single method/action/property body only
```

Never fetch a full POU when you only need one method.

| Method | Returns |
|--------|---------|
| `get_structure(project_path)` | `ProjectStructure` |
| `get_pou_interface(pou_name)` | `POUInterface` |
| `get_pou_item(pou_name, item_name)` | `POUItem` |
| `get_gvl(gvl_name)` | `GVL` |
| `get_dut(dut_name)` | `DUT` |

`get_pou_item` accepts dotted property accessor syntax: `"Status.Get"` / `"Status.Set"`.

## ProjectWriter

**File:** `tckit/ports/writer.py`  
**Purpose:** Structural writes via the automation interface (bridge → COM).

Never manipulate `.TcPOU` XML or `.plcproj` files directly for structural changes. The automation interface handles GUID assignment, `.plcproj` cross-references, and tree indexing.

| Method | Returns |
|--------|---------|
| `open_project(solution_path)` | `Result` |
| `create_project(name, path)` | `Result` |
| `add_pou(name, pou_type, code)` | `Result` |
| `add_method(pou_name, method_name, code)` | `Result` |
| `update_pou_item(pou_name, item_name, code)` | `Result` |

## BuildRunner

**File:** `tckit/ports/builder.py`  
**Purpose:** Build, deploy, and runtime control.

Returns structured errors with `file`, `line`, `message`, `severity`. Never deploy without a successful build first.

| Method | Returns |
|--------|---------|
| `build(project_path)` | `BuildResult` |
| `deploy(target_ams_id)` | `Result` |
| `start_runtime(target_ams_id)` | `Result` |
| `get_status()` | `BuildStatus` |

## TestRunner

**File:** `tckit/ports/test_runner.py`  
**Purpose:** TcUnit test execution and result parsing.

| Method | Returns |
|--------|---------|
| `run_tests()` | `Result` |
| `wait_complete(timeout_seconds)` | `Result` |
| `get_results()` | `TestResults` |
| `get_status()` | `TestStatus` |

## DocGenerator

**File:** `tckit/ports/doc_generator.py`  
**Purpose:** Sphinx documentation generation from RST-commented ST source.

Trigger modes: `on_demand` | `on_build` (default).

| Method | Returns |
|--------|---------|
| `generate(project_path, output_path)` | `Result` |
| `get_status()` | `DocStatus` |

## DocsSearcher

**File:** `tckit/ports/docs_searcher.py`  
**Purpose:** Search and fetch Beckhoff infosys documentation.

Always call `find_fb()` before writing code that uses an unfamiliar Beckhoff FB.

| Method | Returns |
|--------|---------|
| `find_fb(fb_name)` | `FBDoc` |
| `find_library(library_name)` | `LibraryDoc` |
| `search(query, section)` | `SearchResults` |
| `get_page(url)` | `DocPage` |
