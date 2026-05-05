# xml_reader

**Class:** `XmlReader`  
**Config key:** `"reader": "xml"`  
**File:** `tckit/adapters/readers/xml_reader.py`

---

## Overview

`xml_reader` is the default `ProjectReader` adapter. It reads `.TcPOU` and `.TcGVL` files directly from the filesystem using Python's built-in `xml.etree.ElementTree` library.

**No third-party dependencies.** No Windows, XAE, or blark required. Runs inside Docker.

---

## How it works

TwinCAT 3 stores each POU as an XML file where ST code lives in CDATA sections:

```xml
<TcPlcObject Version="1.1.0.1" ProductVersion="3.1.4026.0">
  <POU Name="FB_Example" Id="{...}">
    <Declaration><![CDATA[
FUNCTION_BLOCK FB_Example
VAR_INPUT
    bEnable : BOOL;
END_VAR
    ]]></Declaration>
    <Implementation>
      <ST><![CDATA[
(* body code here *)
      ]]></ST>
    </Implementation>
    <Method Name="Execute" Id="{...}">
      ...
    </Method>
  </POU>
</TcPlcObject>
```

All structural information (POU name, type, method names, action names, property names) comes from XML attributes and element names. ST code is returned as raw strings — no ST grammar parsing is performed.

Method return types are extracted with a single regex:

```python
re.search(r"METHOD\s+\w+\s*:\s*(\w+)", declaration_text)
```

---

## Internal structure

```
tckit/adapters/readers/
├── xml_reader.py           ← XmlReader class (public)
└── _tcpou_parser.py        ← XML/CDATA extraction utilities (private)
```

`_tcpou_parser.py` imports only stdlib (`xml.etree.ElementTree`, `re`, `pathlib`).

---

## Configuration

```json
{
  "reader": "xml"
}
```

No additional config keys required. The adapter discovers files from the `project_path` argument passed to `get_structure()`.

Set `PLC_PROJECT_PATH` in your `.env` as a fallback when `get_structure()` hasn't been called:

```env
PLC_PROJECT_PATH=/path/to/my/plc/project
```

---

## Methods

### `get_structure(project_path)`

Recursively scans `project_path` for `*.TcPOU` and `*.TcGVL` files. Populates an internal file index (name → path) reused by all subsequent calls.

```python
structure = reader.get_structure("/projects/MyPLC")
# structure.pous → [POURef(name="FB_Motor", pou_type=FUNCTION_BLOCK, ...), ...]
# structure.gvls → ["GVL_Params", ...]
```

### `get_pou_interface(pou_name)`

Returns declarations and method signatures for a POU. Does **not** return method bodies.

```python
interface = reader.get_pou_interface("FB_Motor")
# interface.declaration   → VAR_INPUT / VAR_OUTPUT block as raw ST text
# interface.methods       → [MethodSignature(name="Execute", return_type="BOOL", ...)]
# interface.actions       → ["Action1", ...]
# interface.properties    → ["Status", ...]
```

### `get_pou_item(pou_name, item_name)`

Returns the declaration and body of a single method or action. Use this when you only need one method — never fetch the full POU.

```python
item = reader.get_pou_item("FB_Motor", "Execute")
# item.declaration → METHOD header + VAR_INPUT block
# item.body        → ST implementation code
```

### `get_gvl(gvl_name)`

Returns the declaration block of a Global Variable List.

```python
gvl = reader.get_gvl("GVL_Params")
# gvl.declaration → VAR_GLOBAL block as raw ST text
```

---

## Supported file types

| Extension | Element | Notes |
|-----------|---------|-------|
| `.TcPOU`  | `<POU>` | Function blocks, functions, programs, interfaces |
| `.TcGVL`  | `<GVL>` | Global variable lists |

Tasks (`.TcTTO`) are listed in the project structure but not parsed in Phase 1.

---

## Error handling

| Situation | Exception |
|-----------|-----------|
| `project_path` does not exist | `FileNotFoundError` |
| POU/GVL name not in file index | `FileNotFoundError` |
| File cannot be parsed as XML | `ValueError` |
| Method/action name not found in POU | `FileNotFoundError` |

Malformed ST code inside CDATA is never an error — it is returned as-is.
