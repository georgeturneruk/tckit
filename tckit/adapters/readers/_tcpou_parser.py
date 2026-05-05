"""_tcpou_parser — private XML/CDATA utilities for .TcPOU, .TcGVL, and .TcDUT files.

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


def extract_method_return_type(declaration: str) -> str:
    """Extract return type from a METHOD declaration using regex.

    Matches:  METHOD MethodName : ReturnType
    Returns empty string if not found.
    """
    match = re.search(r"METHOD\s+\w+\s*:\s*(\w+)", declaration, re.IGNORECASE)
    return match.group(1) if match else ""


def extract_property_return_type(declaration: str) -> str:
    """Extract return type from a PROPERTY declaration using regex.

    Matches:  PROPERTY PropertyName : ReturnType
    Returns empty string if not found.
    """
    match = re.search(r"PROPERTY\s+\w+\s*:\s*(\w+)", declaration, re.IGNORECASE)
    return match.group(1) if match else ""


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
