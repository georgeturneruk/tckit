"""_comment_extractor — auto-detect and parse doc comments from ST declaration strings.

Supports three comment styles found in TwinCAT projects:

  xml_docu   (*~ <docu><summary>...</summary></docu> ~*)   TcOpen / Beckhoff TE1030
  block_rst  (* :Description: ...\n:param x: ... *)        plcdoc convention
  line_rst   // :Description: ...\n// :param x: ...        Common informal style
  plain      // some comment                               No structured docs

All styles are auto-detected. The extracted content is normalised into CommentDoc.
"""

from __future__ import annotations

import re
import xml.etree.ElementTree as ET

from tckit.ports.types import CommentDoc

# ---------------------------------------------------------------------------
# PLC keyword list — preamble ends at the first of these
# ---------------------------------------------------------------------------

_KEYWORDS = (
    "FUNCTION_BLOCK",
    "FUNCTION",
    "PROGRAM",
    "INTERFACE",
    "METHOD",
    "PROPERTY",
    "TYPE",
)

# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------


def extract_comment(declaration: str) -> CommentDoc:
    """Extract and parse a doc comment from a ST declaration string.

    Scans the text before the first PLC keyword, detects the comment style,
    and returns a normalised CommentDoc.  Returns an empty CommentDoc if no
    structured comment is found.

    Args:
        declaration: Raw Declaration CDATA text from a .TcPOU/.TcGVL/.TcDUT file.
    """
    preamble = _extract_preamble(declaration)
    if not preamble.strip():
        return CommentDoc()

    style = _detect_style(preamble)

    if style == "xml_docu":
        return _parse_xml_docu(preamble)
    if style in ("block_rst", "line_rst"):
        text = _normalise_to_lines(preamble, style)
        return _parse_rst_lines(text)
    return CommentDoc()


# ---------------------------------------------------------------------------
# Preamble extraction
# ---------------------------------------------------------------------------


def _extract_preamble(declaration: str) -> str:
    """Return the text before the first PLC keyword.

    Keywords are only matched at the start of a line (after stripping
    whitespace) so that keyword words appearing inside comment text are
    not mistakenly treated as declaration boundaries.
    """
    lines = declaration.splitlines(keepends=True)
    collected: list[str] = []
    for line in lines:
        stripped = line.strip().upper()
        if any(stripped.startswith(kw) for kw in _KEYWORDS):
            break
        collected.append(line)
    return "".join(collected)


# ---------------------------------------------------------------------------
# Style detection
# ---------------------------------------------------------------------------


def _detect_style(preamble: str) -> str:
    """Detect the comment style in the preamble text."""
    if "(*~" in preamble:
        return "xml_docu"
    if "(*" in preamble:
        return "block_rst"
    # Check if any non-empty line starts with //
    for line in preamble.splitlines():
        stripped = line.strip()
        if stripped and stripped.startswith("//"):
            return "line_rst"
    return "plain"


# ---------------------------------------------------------------------------
# XML <docu> parser
# ---------------------------------------------------------------------------


def _parse_xml_docu(preamble: str) -> CommentDoc:
    """Extract from (*~ <docu>...</docu> ~*) XML-style block comments."""
    # Extract content between (*~ and ~*)
    match = re.search(r"\(\*~(.*?)~\*\)", preamble, re.DOTALL)
    if not match:
        # Fallback: try plain (*  *)
        match = re.search(r"\(\*(.*?)\*\)", preamble, re.DOTALL)
    if not match:
        return CommentDoc()

    raw = match.group(1).strip()

    # Try parsing as XML
    try:
        root = ET.fromstring(f"<root>{raw}</root>")
    except ET.ParseError:
        # Not valid XML — fall through to RST parser
        return _parse_rst_lines(_strip_comment_markers(raw))

    description = _xml_text(root, "summary") or _xml_text(root, "description") or ""
    returns = _xml_text(root, "returns") or ""
    remarks = _xml_text(root, "remarks") or ""

    params: dict[str, str] = {}
    for param_el in root.iter("param"):
        name = param_el.get("name") or (param_el.text or "").split()[0] if param_el.text else ""
        text = _clean_xml_text(param_el.text or "")
        if name:
            params[name.strip()] = text

    return CommentDoc(
        description=_clean_xml_text(description),
        params=params,
        returns=_clean_xml_text(returns),
        remarks=_clean_xml_text(remarks),
    )


def _xml_text(root: ET.Element, tag: str) -> str:
    """Return concatenated text from all matching child elements."""
    parts = []
    for el in root.iter(tag):
        parts.append("".join(el.itertext()))
    return " ".join(p.strip() for p in parts if p.strip())


def _clean_xml_text(text: str) -> str:
    """Collapse whitespace and strip XML artefacts."""
    # Collapse newlines and multiple spaces
    text = re.sub(r"\s+", " ", text).strip()
    return text


# ---------------------------------------------------------------------------
# RST-style parser (line_rst and block_rst)
# ---------------------------------------------------------------------------


def _normalise_to_lines(preamble: str, style: str) -> str:
    """Strip comment delimiters and return clean text lines."""
    if style == "block_rst":
        # Strip (*, *), ~, leading/trailing whitespace
        text = re.sub(r"\(\*~?", "", preamble)
        text = re.sub(r"~?\*\)", "", text)
        return text.strip()
    # line_rst — strip leading // from each line, skip attribute pragmas
    lines = []
    for line in preamble.splitlines():
        stripped = line.strip()
        if stripped.startswith("{"):
            continue  # skip {attribute ...} pragmas
        if stripped.startswith("//"):
            lines.append(stripped[2:].strip())
        # non-comment non-empty lines are silently dropped in line_rst mode
    return "\n".join(lines)


def _strip_comment_markers(text: str) -> str:
    """Strip common comment markers for fallback cases."""
    text = re.sub(r"\(\*~?", "", text)
    text = re.sub(r"~?\*\)", "", text)
    for line in text.splitlines():
        s = line.strip()
        if s.startswith("//"):
            return "\n".join(
                ln.strip().lstrip("/").strip() for ln in text.splitlines()
            )
    return text.strip()


# Matches :param name: value (two colons, name argument)
_PARAM_RE = re.compile(r"^:param\s+(?P<name>\w+):\s*(?P<value>.*)$")
# Matches :field: value (single colon, no name argument)
_FIELD_RE = re.compile(r"^:(?P<field>\w[\w ]*?):\s*(?P<value>.*)$")


def _parse_rst_lines(text: str) -> CommentDoc:
    """Parse RST field-list style documentation.

    Supports:
        :Description: text       → description
        :param name: text        → params["name"]
        :returns: text           → returns
        :remarks: text           → remarks

    Any text before the first field line is also treated as description.
    """
    lines = text.splitlines()
    description_lines: list[str] = []
    params: dict[str, str] = {}
    returns = ""
    remarks = ""

    in_description = True
    current_field: tuple[str, str] | None = None  # (field_type, accumulated_value)

    def _flush(field: tuple[str, str] | None) -> None:
        nonlocal returns, remarks
        if field is None:
            return
        ftype, value = field
        value = value.strip()
        if ftype == "description":
            description_lines.append(value)
        elif ftype == "returns":
            returns = value
        elif ftype == "remarks":
            remarks = value

    for line in lines:
        stripped = line.strip()
        # Try :param name: value first (two-colon form)
        pm = _PARAM_RE.match(stripped)
        if pm:
            _flush(current_field)
            in_description = False
            current_field = None
            params[pm.group("name")] = pm.group("value").strip()
            continue

        # Try :field: value (single-colon form)
        m = _FIELD_RE.match(stripped)
        if m:
            _flush(current_field)
            in_description = False
            field_name = m.group("field").lower()
            value = m.group("value").strip()

            if field_name in ("description", "summary"):
                current_field = ("description", value)
            elif field_name in ("returns", "return"):
                current_field = ("returns", value)
            elif field_name == "remarks":
                current_field = ("remarks", value)
            else:
                current_field = None
        elif in_description and stripped:
            description_lines.append(stripped)
        elif current_field and stripped:
            # Continuation line
            ftype, val = current_field
            current_field = (ftype, val + " " + stripped)

    _flush(current_field)

    description = " ".join(description_lines).strip()
    return CommentDoc(description=description, params=params, returns=returns, remarks=remarks)
