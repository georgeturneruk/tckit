"""_tcpou_parser — private XML/CDATA utilities for .TcPOU and .TcGVL files.

Stdlib only: xml.etree.ElementTree, re, pathlib.
No third-party dependencies.

TwinCAT file structure:
  .TcPOU — contains a single <POU> element with optional <Method>, <Action>,
            <Property> children. All ST code lives in CDATA sections.
  .TcGVL  — contains a single <GVL> element with a <Declaration> CDATA.
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


def get_declaration(pou_or_method: ET.Element) -> str:
    """Extract Declaration CDATA from a <POU>, <Method>, <Action>, or <GVL> element."""
    return get_cdata(pou_or_method.find("Declaration"))


def get_st_body(pou_or_method: ET.Element) -> str:
    """Extract ST implementation body from Implementation/ST CDATA."""
    impl = pou_or_method.find("Implementation")
    if impl is None:
        return ""
    st = impl.find("ST")
    return get_cdata(st)


# ---------------------------------------------------------------------------
# POU-level extraction
# ---------------------------------------------------------------------------


def detect_pou_type(declaration: str) -> str:
    """Detect POUType from the first keyword in the declaration text.

    Returns one of: "function_block", "function", "program", "interface".
    Defaults to "function_block" if no keyword is found.
    """
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


# ---------------------------------------------------------------------------
# File-level parsers
# ---------------------------------------------------------------------------


def parse_tcpou(path: Path) -> dict:
    """Parse a .TcPOU file and return a dict with POU structure.

    Returns:
        {
            "name": str,
            "pou_type": str,
            "declaration": str,
            "body": str,
            "methods": [{"name": str, "declaration": str, "body": str}],
            "actions": [{"name": str, "declaration": str, "body": str}],
            "properties": [str],   # property names only
        }
    """
    root = parse_file(path)
    pou_el = root.find("POU")
    if pou_el is None:
        raise ValueError(f"No <POU> element found in {path}")

    name = pou_el.get("Name", "")
    declaration = get_declaration(pou_el)
    body = get_st_body(pou_el)
    pou_type = detect_pou_type(declaration)

    methods = []
    for method_el in pou_el.findall("Method"):
        method_name = method_el.get("Name", "")
        method_decl = get_declaration(method_el)
        method_body = get_st_body(method_el)
        methods.append(
            {"name": method_name, "declaration": method_decl, "body": method_body}
        )

    actions = []
    for action_el in pou_el.findall("Action"):
        action_name = action_el.get("Name", "")
        action_decl = get_declaration(action_el)
        action_body = get_st_body(action_el)
        actions.append(
            {"name": action_name, "declaration": action_decl, "body": action_body}
        )

    properties = [
        prop_el.get("Name", "") for prop_el in pou_el.findall("Property")
    ]

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

    name = gvl_el.get("Name", "")
    declaration = get_declaration(gvl_el)
    return {"name": name, "declaration": declaration}
