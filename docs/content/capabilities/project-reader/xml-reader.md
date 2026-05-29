# xml_reader

**Class:** `XmlReader`  
**Config key:** `"reader": "xml"`  
**File:** `tckit/adapters/readers/xml_reader.py`

---

## Overview

`xml_reader` is the default `ProjectReader` adapter. It reads `.TcPOU`, `.TcGVL`, and `.TcDUT` files directly from the filesystem using Python's built-in `xml.etree.ElementTree` library.

**No third-party dependencies.** No Windows, XAE, or blark required. Runs inside Docker.

---

## How it works

TwinCAT 3 stores each POU as an XML file where ST code lives in CDATA sections. The parser reads XML structure for names and types, and returns ST text verbatim — no grammar parsing is performed.

**Function blocks, functions, programs** use a `<POU>` root element:

```xml
<TcPlcObject Version="1.1.0.1" ProductVersion="3.1.4026.0">
  <POU Name="FB_Motor" Id="{...}">
    <Declaration><![CDATA[
FUNCTION_BLOCK FB_Motor
VAR_INPUT
    bEnable : BOOL;
END_VAR
    ]]></Declaration>
    <Implementation>
      <ST><![CDATA[ ... ]]></ST>
    </Implementation>
    <Method Name="Execute" Id="{...}"> ... </Method>
    <Property Name="Status" Id="{...}">
      <Declaration><![CDATA[PROPERTY Status : DWORD]]></Declaration>
      <Get Id="{...}"><Implementation><ST><![CDATA[ ... ]]></ST></Implementation></Get>
      <Set Id="{...}"><Implementation><ST><![CDATA[ ... ]]></ST></Implementation></Set>
    </Property>
  </POU>
</TcPlcObject>
```

**Interfaces** use an `<Itf>` root element (same file extension, `.TcPOU`):

```xml
<TcPlcObject Version="1.1.0.1" ProductVersion="3.1.4026.0">
  <Itf Name="I_Motor" Id="{...}">
    <Declaration><![CDATA[INTERFACE I_Motor]]></Declaration>
    <Method Name="Execute" Id="{...}"> ... </Method>
    <Property Name="Status" Id="{...}"> ... </Property>
  </Itf>
</TcPlcObject>
```

**Data types** (`.TcDUT`) hold TYPE definitions — structs, enums, unions, aliases:

```xml
<TcPlcObject Version="1.1.0.1" ProductVersion="3.1.4026.0">
  <DUT Name="ST_MotorConfig" Id="{...}">
    <Declaration><![CDATA[
TYPE ST_MotorConfig :
STRUCT
    nMaxSpeed : INT;
    bEnabled  : BOOL;
END_STRUCT
END_TYPE
    ]]></Declaration>
  </DUT>
</TcPlcObject>
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

Set `PLC_PROJECT_PATH` in your `.env` as a fallback when `get_structure()` hasn't been called yet:

```env
PLC_PROJECT_PATH=/path/to/my/plc/project
```

---

## Methods

### `get_structure(project_path)`

Recursively scans `project_path` for `*.TcPOU`, `*.TcGVL`, and `*.TcDUT` files. Populates an internal file index (name → path) reused by all subsequent calls.

`project_path` accepts either the solution root directory or a `.sln` file inside it; the `.sln` form is treated as a shorthand for its parent directory.

```python
structure = reader.get_structure("/projects/MyPLC")
section = structure.plcs["MyPLC"]
# section.pous → [POURef(name="FB_Motor", pou_type=FUNCTION_BLOCK, folder="Drives", ...), ...]
# section.gvls → [GVLRef(name="GVL_Params", folder="", ...), ...]
# section.duts → [DUTRef(name="ST_MotorConfig", dut_kind="struct", folder="", ...),
#                 DUTRef(name="Counter", dut_kind="alias", ...), ...]
```

### `get_pou_interface(pou_name)`

Returns declarations and method/property signatures for any POU or interface. Does **not** return method or property bodies.

```python
interface = reader.get_pou_interface("FB_Motor")
# interface.declaration   → VAR_INPUT / VAR_OUTPUT block as raw ST text
# interface.pou_type      → POUType.FUNCTION_BLOCK
# interface.methods       → [MethodSignature(name="Execute", return_type="BOOL", ...)]
# interface.properties    → [PropertySignature(name="Status", return_type="DWORD",
#                                              has_get=True, has_set=True, ...)]
# interface.actions       → ["ActionName", ...]

interface = reader.get_pou_interface("I_Motor")
# interface.pou_type      → POUType.INTERFACE
```

### `get_pou_item(pou_name, item_name)`

Returns the declaration and body of a single method, action, or property accessor. Use this when you only need one item — never fetch the full POU.

```python
# Method or action
item = reader.get_pou_item("FB_Motor", "Execute")
# item.declaration → METHOD header + VAR_INPUT block
# item.body        → ST implementation code

# Property getter
item = reader.get_pou_item("FB_Motor", "Status.Get")
# item.body → getter ST code

# Property setter
item = reader.get_pou_item("FB_Motor", "Status.Set")
# item.body → setter ST code

# Property header only (no body)
item = reader.get_pou_item("FB_Motor", "Status")
# item.declaration → PROPERTY Status : DWORD
# item.body        → ""
```

### `get_gvl(gvl_name)`

Returns the declaration block of a Global Variable List.

```python
gvl = reader.get_gvl("GVL_Params")
# gvl.declaration → VAR_GLOBAL block as raw ST text
```

### `get_dut(dut_name)`

Returns the full TYPE definition of a Data Unit Type (struct, enum, union, alias).

```python
dut = reader.get_dut("ST_MotorConfig")
# dut.declaration → TYPE ST_MotorConfig : STRUCT ... END_STRUCT END_TYPE

dut = reader.get_dut("E_State")
# dut.declaration → TYPE E_State : (Idle := 0, Running := 1, ...) END_TYPE
```

---

## Supported file types

| Extension | XML element | Contents |
|-----------|-------------|----------|
| `.TcPOU`  | `<POU>`     | Function blocks, functions, programs |
| `.TcPOU`  | `<Itf>`     | Interfaces |
| `.TcGVL`  | `<GVL>`     | Global variable lists |
| `.TcDUT`  | `<DUT>`     | Structs, enums, unions, type aliases |

Tasks (`.TcTTO`) are discovered by `get_structure()` but not parsed — task configuration is not needed for code reading.

---

## Error handling

| Situation | Exception |
|-----------|-----------|
| `project_path` does not exist | `FileNotFoundError` |
| POU/GVL/DUT name not in file index | `FileNotFoundError` |
| File cannot be parsed as XML | `ValueError` |
| Method/action/property not found | `FileNotFoundError` |
| Property accessor (`.Get`/`.Set`) not present | `FileNotFoundError` |

Malformed ST code inside CDATA is never an error — it is returned as-is.
