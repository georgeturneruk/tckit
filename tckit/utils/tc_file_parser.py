"""tc_file_parser — XML/CDATA utilities for .TcPOU, .TcGVL, and .TcDUT files.

Shared utility used by reader and doc-generator adapters. Lives under
``tckit/utils/`` so adapters can depend on it without violating the
adapter-isolation rule (no adapter-to-adapter imports).

Stdlib only: xml.etree.ElementTree, re, pathlib.
No third-party dependencies.

TwinCAT file structure:
  .TcPOU — XML root contains either:
              <POU>  — for FUNCTION_BLOCK, FUNCTION, PROGRAM
              <Itf>  — for INTERFACE
            Both have optional <Method>, <Action>, <Property> children.
            <Property> elements contain <Get> and/or <Set> sub-elements.
  .TcGVL  — XML root contains a <GVL> element with a <Declaration> CDATA.
  .TcDUT  — XML root contains a <DUT> element with a <Declaration> CDATA
            (STRUCT, ENUM, UNION, or TYPE alias definitions).
"""

import re
import xml.etree.ElementTree as ET
from pathlib import Path

# ---------------------------------------------------------------------------
# Low-level XML helpers
# ---------------------------------------------------------------------------


def parse_file(path: Path) -> ET.Element:
    """Parse an XML file and return the root element.

    Raises:
        ValueError: If the file cannot be parsed as XML.
    """
    try:
        tree = ET.parse(path)
    except ET.ParseError as exc:
        raise ValueError(f"XML parse error in {path}: {exc}") from exc
    return tree.getroot()


def get_cdata(element: ET.Element | None) -> str:
    """Return the text content of an element, stripped of leading/trailing whitespace.

    ElementTree doesn't distinguish CDATA from plain text — the content is
    already decoded. Returns empty string if element is None or has no text.
    """
    if element is None:
        return ""
    return (element.text or "").strip()


def get_declaration(element: ET.Element) -> str:
    """Extract Declaration CDATA from a <POU>, <Itf>, <Method>, <Action>, etc."""
    return get_cdata(element.find("Declaration"))


def get_st_body(element: ET.Element) -> str:
    """Extract ST implementation body from Implementation/ST CDATA."""
    impl = element.find("Implementation")
    if impl is None:
        return ""
    st = impl.find("ST")
    return get_cdata(st)


# ---------------------------------------------------------------------------
# POU / Interface type detection
# ---------------------------------------------------------------------------


def detect_pou_type(declaration: str, element_tag: str = "POU") -> str:
    """Detect POUType from the element tag and declaration text.

    <Itf> elements are always "interface".
    <POU> elements are detected from the first keyword in the declaration.
    Defaults to "function_block" if no keyword is found.
    """
    if element_tag.lower() == "itf":
        return "interface"
    text = declaration.upper()
    for keyword, pou_type in (
        ("FUNCTION_BLOCK", "function_block"),
        ("FUNCTION", "function"),
        ("PROGRAM", "program"),
        ("INTERFACE", "interface"),
    ):
        if keyword in text:
            return pou_type
    return "function_block"


# Access modifiers (TwinCAT IEC 61131-3 ext): may appear between the
# METHOD / PROPERTY keyword and the name. XAE writes them when the
# accessor is created via the automation interface (CreateChild vInfo
# carries the access string), so a declaration produced by add_property
# / add_method looks like ``PROPERTY PUBLIC Foo : LREAL`` rather than
# ``PROPERTY Foo : LREAL``. Both spellings must round-trip cleanly.
_ACCESS_MOD = r"(?:(?:PUBLIC|PRIVATE|PROTECTED|INTERNAL|FINAL|ABSTRACT)\s+)*"


def extract_method_return_type(declaration: str) -> str:
    """Extract return type from a METHOD declaration using regex.

    Matches:  METHOD [<access>...] MethodName : ReturnType
    Returns empty string if not found.
    """
    match = re.search(
        rf"METHOD\s+{_ACCESS_MOD}\w+\s*:\s*(\w+)",
        declaration,
        re.IGNORECASE,
    )
    return match.group(1) if match else ""


def extract_property_return_type(declaration: str) -> str:
    """Extract return type from a PROPERTY declaration using regex.

    Matches:  PROPERTY [<access>...] PropertyName : ReturnType
    Returns empty string if not found.
    """
    match = re.search(
        rf"PROPERTY\s+{_ACCESS_MOD}\w+\s*:\s*(\w+)",
        declaration,
        re.IGNORECASE,
    )
    return match.group(1) if match else ""


# Matches an implementation-only VAR block:
#   VAR  ... END_VAR        (method locals)
#   VAR_TEMP ... END_VAR    (per-call temporaries)
#   VAR CONSTANT ... END_VAR (internal constants)
# Each block opener must be the only token on its line so we don't accidentally
# match VAR_INPUT/VAR_OUTPUT/VAR_IN_OUT/VAR_INST, which are part of the API
# surface and must be preserved.
_LOCAL_VAR_BLOCK_RE = re.compile(
    r"^[ \t]*(?:VAR(?:[ \t]+CONSTANT)?|VAR_TEMP)[ \t]*\r?\n.*?^[ \t]*END_VAR[ \t]*\r?\n?",
    re.MULTILINE | re.DOTALL | re.IGNORECASE,
)


def strip_method_locals(declaration: str) -> str:
    """Strip implementation-only VAR blocks from a method declaration.

    Removes VAR (locals), VAR_TEMP, and VAR CONSTANT blocks while preserving
    VAR_INPUT, VAR_OUTPUT, VAR_IN_OUT, and VAR_INST (the API surface).

    Used when building MethodSignature payloads for ``get_pou_interface`` so
    the interface call doesn't carry implementation detail. ``get_pou_item``
    must NOT call this — callers asking for a method body need its locals.
    """
    return _LOCAL_VAR_BLOCK_RE.sub("", declaration).rstrip()


# ---------------------------------------------------------------------------
# File-level parsers
# ---------------------------------------------------------------------------


def parse_tcpou(path: Path) -> dict:
    """Parse a .TcPOU file and return a dict with POU/Interface structure.

    Handles both <POU> (function blocks, functions, programs) and
    <Itf> (interfaces) root elements.

    Returns:
        {
            "name": str,
            "pou_type": str,             # "function_block"|"function"|"program"|"interface"
            "declaration": str,
            "body": str,
            "methods": [{"name": str, "declaration": str, "body": str}],
            "actions":  [{"name": str, "declaration": str, "body": str}],
            "properties": [
                {
                    "name": str,
                    "declaration": str,  # PROPERTY header
                    "get": {"declaration": str, "body": str} | None,
                    "set": {"declaration": str, "body": str} | None,
                }
            ],
        }
    """
    root = parse_file(path)

    # TwinCAT stores POUs as <POU> and interfaces as <Itf>
    pou_el = root.find("POU")
    itf_el = root.find("Itf")
    container = pou_el if pou_el is not None else itf_el
    if container is None:
        raise ValueError(f"No <POU> or <Itf> element found in {path}")

    tag = container.tag  # "POU" or "Itf"
    name = container.get("Name", "")
    declaration = get_declaration(container)
    body = get_st_body(container)
    pou_type = detect_pou_type(declaration, tag)

    methods = []
    for method_el in container.findall("Method"):
        methods.append(
            {
                "name": method_el.get("Name", ""),
                "declaration": get_declaration(method_el),
                "body": get_st_body(method_el),
            }
        )

    actions = []
    for action_el in container.findall("Action"):
        actions.append(
            {
                "name": action_el.get("Name", ""),
                "declaration": get_declaration(action_el),
                "body": get_st_body(action_el),
            }
        )

    properties = []
    for prop_el in container.findall("Property"):
        prop_name = prop_el.get("Name", "")
        prop_decl = get_declaration(prop_el)

        get_el = prop_el.find("Get")
        set_el = prop_el.find("Set")

        prop_get = (
            {"declaration": get_declaration(get_el), "body": get_st_body(get_el)}
            if get_el is not None
            else None
        )
        prop_set = (
            {"declaration": get_declaration(set_el), "body": get_st_body(set_el)}
            if set_el is not None
            else None
        )

        properties.append(
            {
                "name": prop_name,
                "declaration": prop_decl,
                "get": prop_get,
                "set": prop_set,
            }
        )

    return {
        "name": name,
        "pou_type": pou_type,
        "declaration": declaration,
        "body": body,
        "methods": methods,
        "actions": actions,
        "properties": properties,
    }


def parse_tcgvl(path: Path) -> dict:
    """Parse a .TcGVL file and return a dict with GVL structure.

    Returns:
        {"name": str, "declaration": str}
    """
    root = parse_file(path)
    gvl_el = root.find("GVL")
    if gvl_el is None:
        raise ValueError(f"No <GVL> element found in {path}")

    return {"name": gvl_el.get("Name", ""), "declaration": get_declaration(gvl_el)}


def parse_tcdut(path: Path) -> dict:
    """Parse a .TcDUT file and return a dict with DUT structure.

    DUTs include STRUCT, ENUM, UNION, and TYPE alias definitions.

    Returns:
        {"name": str, "declaration": str}
    """
    root = parse_file(path)
    dut_el = root.find("DUT")
    if dut_el is None:
        raise ValueError(f"No <DUT> element found in {path}")

    return {"name": dut_el.get("Name", ""), "declaration": get_declaration(dut_el)}


# ---------------------------------------------------------------------------
# Project-file parsers (.plcproj / .tsproj / .TcTTO)
# ---------------------------------------------------------------------------


def _local(tag: str) -> str:
    """Strip XML namespace from a tag, e.g. '{ns}Project' -> 'Project'."""
    return tag.split("}", 1)[-1] if "}" in tag else tag


def _to_int(value: str | None) -> int | None:
    if value is None:
        return None
    try:
        return int(value.strip())
    except (ValueError, TypeError, AttributeError):
        return None


def _child_text(element: ET.Element, local_name: str) -> str:
    for child in element:
        if _local(child.tag) == local_name:
            return (child.text or "").strip()
    return ""


def _split_resolution(resolution: str, fallback_name: str) -> tuple[str, str]:
    """Parse a placeholder DefaultResolution like 'Name, * (Vendor)' into (version, name)."""
    if not resolution:
        return "", fallback_name
    if "," in resolution:
        name_part, rest = resolution.split(",", 1)
        version_part = rest.strip()
        paren_idx = version_part.find("(")
        version = version_part[:paren_idx].strip() if paren_idx >= 0 else version_part
        return version, name_part.strip()
    return "", resolution.strip()


def parse_plcproj(path: Path) -> dict:
    """Parse a .plcproj for library references.

    The .plcproj uses the MSBuild XML namespace
    (http://schemas.microsoft.com/developer/msbuild/2003). Library refs live
    in <ItemGroup> elements as either <PlaceholderReference> (versioned via a
    DefaultResolution string of the form 'Name, version (Vendor)') or
    <LibraryReference> (Include attribute formatted 'Name,version,Vendor').

    Returns:
        {"libraries": [{"name": str, "version": str, "placeholder": str | None}]}
    """
    root = parse_file(path)
    libraries: list[dict] = []

    for item_group in root:
        if _local(item_group.tag) != "ItemGroup":
            continue
        for ref in item_group:
            tag = _local(ref.tag)
            include = ref.get("Include", "").strip()
            if tag == "PlaceholderReference":
                resolution = _child_text(ref, "DefaultResolution")
                version, resolved_name = _split_resolution(resolution, include)
                libraries.append({
                    "name": resolved_name or include,
                    "version": version,
                    "placeholder": include or None,
                })
            elif tag == "LibraryReference":
                parts = [p.strip() for p in include.split(",")]
                name = parts[0] if parts else include
                version = parts[1] if len(parts) > 1 else ""
                libraries.append({
                    "name": name,
                    "version": version,
                    "placeholder": None,
                })

    return {"libraries": libraries}


def parse_tsproj(path: Path) -> dict:
    """Parse a .tsproj for System Manager task definitions.

    .tsproj CycleTime is in 100ns ticks; converted to microseconds here for
    consistency with .TcTTO. POU bindings live in .TcTTO, not .tsproj, so the
    'programs' list is always empty from this source.

    Returns:
        {"tasks": [{"name": str, "cycle_time_us": int | None,
                    "priority": int | None, "programs": []}]}
    """
    root = parse_file(path)
    tasks: list[dict] = []

    for task_el in root.iter():
        if _local(task_el.tag) != "Task":
            continue
        # System-manager tasks carry numeric Id + CycleTime attributes;
        # filters out unrelated <Task> nodes that may appear elsewhere.
        if task_el.get("Id") is None or task_el.get("CycleTime") is None:
            continue
        name = _child_text(task_el, "Name")
        if not name:
            continue
        cycle_ticks = _to_int(task_el.get("CycleTime"))
        cycle_us = cycle_ticks // 10 if cycle_ticks is not None else None
        priority = _to_int(task_el.get("Priority"))
        tasks.append({
            "name": name,
            "cycle_time_us": cycle_us,
            "priority": priority,
            "programs": [],
        })

    return {"tasks": tasks}


def parse_tctto(path: Path) -> dict:
    """Parse a .TcTTO PLC task object file.

    Authoritative source for PLC task layout: contains the cycle time
    (already in microseconds per Beckhoff's own comment), priority, and the
    POU bound to the task via <PouCall><Name>.

    Returns:
        {"name": str, "cycle_time_us": int | None,
         "priority": int | None, "programs": list[str]}
    """
    root = parse_file(path)
    task_el = root.find("Task")
    if task_el is None:
        raise ValueError(f"No <Task> element found in {path}")

    name = task_el.get("Name", "")
    cycle_us = _to_int(_child_text(task_el, "CycleTime"))
    priority = _to_int(_child_text(task_el, "Priority"))

    programs: list[str] = []
    for child in task_el:
        if _local(child.tag) != "PouCall":
            continue
        pou_name = _child_text(child, "Name")
        if pou_name:
            programs.append(pou_name)

    return {
        "name": name,
        "cycle_time_us": cycle_us,
        "priority": priority,
        "programs": programs,
    }
