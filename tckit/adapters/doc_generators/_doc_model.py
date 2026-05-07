"""_doc_model — build a structured documentation model from a TwinCAT project.

Orchestrates _tc_file_parser (structure) and _comment_extractor (comments)
into a ProjectDoc tree that templates can render directly.
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from pathlib import Path

from tckit.adapters.doc_generators._comment_extractor import extract_comment
from tckit.adapters.readers._tc_file_parser import (
    parse_tcdut,
    parse_tcgvl,
    parse_tcpou,
)
from tckit.ports.types import CommentDoc

# ---------------------------------------------------------------------------
# Doc model dataclasses
# ---------------------------------------------------------------------------


@dataclass
class VariableDoc:
    name: str
    var_type: str
    comment: str = ""


@dataclass
class MethodDoc:
    name: str
    return_type: str
    comment: CommentDoc
    visibility: str = ""          # PUBLIC | PRIVATE | PROTECTED | INTERNAL
    is_abstract: bool = False
    is_final: bool = False
    inputs: list[VariableDoc] = field(default_factory=list)
    outputs: list[VariableDoc] = field(default_factory=list)
    inout: list[VariableDoc] = field(default_factory=list)
    body: str = ""


@dataclass
class PropertyDoc:
    name: str
    return_type: str
    comment: CommentDoc
    visibility: str = ""
    has_get: bool = True
    has_set: bool = False


@dataclass
class ObjectDoc:
    name: str
    obj_type: str   # function_block | function | program | interface | gvl | struct | enum
    declaration: str
    comment: CommentDoc
    visibility: str = ""          # PUBLIC | PRIVATE | PROTECTED | INTERNAL
    is_abstract: bool = False
    is_final: bool = False
    extends: str = ""             # base class name (EXTENDS clause)
    implements: list[str] = field(default_factory=list)  # IMPLEMENTS clause
    inputs: list[VariableDoc] = field(default_factory=list)
    outputs: list[VariableDoc] = field(default_factory=list)
    inout: list[VariableDoc] = field(default_factory=list)
    variables: list[VariableDoc] = field(default_factory=list)
    methods: list[MethodDoc] = field(default_factory=list)
    properties: list[PropertyDoc] = field(default_factory=list)
    actions: list[str] = field(default_factory=list)
    used_by: list[str] = field(default_factory=list)  # names of objects that reference this type


@dataclass
class ProjectDoc:
    name: str
    objects: list[ObjectDoc] = field(default_factory=list)


# ---------------------------------------------------------------------------
# Variable block parser
# ---------------------------------------------------------------------------

_VAR_BLOCK_RE = re.compile(
    r"VAR(?P<kind>_INPUT|_OUTPUT|_IN_OUT|_STAT|_TEMP)?\b.*?END_VAR",
    re.DOTALL | re.IGNORECASE,
)
_VAR_LINE_RE = re.compile(
    r"^\s*(?P<name>[A-Za-z_]\w*)\s*:\s*(?P<type>[^:;=]+?)\s*(?::=.*?)?\s*;"
    r"(?:\s*//\s*(?P<comment>.*))?",
    re.MULTILINE,
)


def _parse_variables(declaration: str) -> dict[str, list[VariableDoc]]:
    """Extract variable lists from a declaration block.

    Returns a dict with keys 'input', 'output', 'inout', 'variable'.
    VAR_IN_OUT is classified separately as 'inout' (bidirectional).
    """
    result: dict[str, list[VariableDoc]] = {
        "input": [],
        "output": [],
        "inout": [],
        "variable": [],
    }
    for block_match in _VAR_BLOCK_RE.finditer(declaration):
        kind = (block_match.group("kind") or "").upper()
        block_text = block_match.group(0)
        category = (
            "input" if kind == "_INPUT"
            else "output" if kind == "_OUTPUT"
            else "inout" if kind == "_IN_OUT"
            else "variable"
        )
        for var_match in _VAR_LINE_RE.finditer(block_text):
            name = var_match.group("name")
            # Skip keywords that can appear inside var blocks
            if name.upper() in ("VAR", "END_VAR", "CONSTANT", "PERSISTENT"):
                continue
            result[category].append(VariableDoc(
                name=name,
                var_type=var_match.group("type").strip(),
                comment=(var_match.group("comment") or "").strip(),
            ))
    return result


# ---------------------------------------------------------------------------
# Public builder
# ---------------------------------------------------------------------------


def _enrich_vars(vars_: list[VariableDoc], params: dict[str, str]) -> list[VariableDoc]:
    """Fill in VariableDoc.comment from CommentDoc.params where inline comment is empty."""
    if not params:
        return vars_
    for v in vars_:
        if not v.comment and v.name in params:
            v.comment = params[v.name]
    return vars_


# Regex to strip ARRAY/POINTER/REFERENCE prefixes and extract the base type name
_BASE_TYPE_RE = re.compile(
    r"(?:ARRAY\s*\[[^\]]*\]\s*OF\s*|POINTER\s+TO\s*|REFERENCE\s+TO\s*)*(\w+)",
    re.IGNORECASE,
)


def _base_type(type_str: str) -> str:
    """Extract the base type name, stripping ARRAY/POINTER/REFERENCE prefixes."""
    m = _BASE_TYPE_RE.match(type_str.strip())
    return m.group(1) if m else type_str.strip()


def _compute_used_by(objects: list[ObjectDoc]) -> None:
    """Populate ObjectDoc.used_by by scanning all type references across the project.

    For each object, scan variable types, method return types, and property
    return types. If any base type name matches a known object, record the
    referencing object's name in the target's used_by list.
    """
    known: dict[str, ObjectDoc] = {obj.name: obj for obj in objects}

    def _record(type_str: str, referencing_name: str) -> None:
        base = _base_type(type_str)
        if base in known and base != referencing_name:
            target = known[base]
            if referencing_name not in target.used_by:
                target.used_by.append(referencing_name)

    for obj in objects:
        for v in obj.inputs + obj.outputs + obj.inout + obj.variables:
            _record(v.var_type, obj.name)
        for m in obj.methods:
            if m.return_type:
                _record(m.return_type, obj.name)
            for v in m.inputs + m.outputs + m.inout:
                _record(v.var_type, obj.name)
        for p in obj.properties:
            if p.return_type:
                _record(p.return_type, obj.name)


def build_project_doc(project_path: str) -> ProjectDoc:
    """Build a full ProjectDoc from a TwinCAT PLC project directory.

    Scans for .TcPOU, .TcGVL, and .TcDUT files, parses each, extracts
    comments, and returns a tree ready for template rendering.

    Args:
        project_path: Absolute path to the TwinCAT PLC project directory.

    Raises:
        ValueError: If no TwinCAT source files are found.
    """
    project = Path(project_path).resolve()
    tcpou_files = sorted(project.glob("**/*.TcPOU"))
    tcgvl_files = sorted(project.glob("**/*.TcGVL"))
    tcdut_files = sorted(project.glob("**/*.TcDUT"))

    if not tcpou_files and not tcgvl_files and not tcdut_files:
        raise ValueError(f"No TwinCAT source files found in {project_path}")

    objects: list[ObjectDoc] = []

    for path in tcpou_files:
        try:
            pou = parse_tcpou(path)
        except Exception:
            continue

        comment = extract_comment(pou["declaration"])
        vars_ = _parse_variables(pou["declaration"])
        _enrich_vars(vars_["input"], comment.params)
        _enrich_vars(vars_["inout"], comment.params)

        meta = _extract_declaration_meta(pou["declaration"])

        methods = []
        for m in pou["methods"]:
            m_comment = extract_comment(m["declaration"])
            m_vars = _parse_variables(m["declaration"])
            m_meta = _extract_declaration_meta(m["declaration"])
            _enrich_vars(m_vars["input"], m_comment.params)
            _enrich_vars(m_vars["output"], m_comment.params)
            methods.append(MethodDoc(
                name=m["name"],
                return_type=_extract_return_type(m["declaration"]),
                comment=m_comment,
                visibility=m_meta["visibility"],
                is_abstract=m_meta["is_abstract"],
                is_final=m_meta["is_final"],
                inputs=m_vars["input"],
                outputs=m_vars["output"],
                inout=m_vars["inout"],
                body=m.get("body", ""),
            ))

        properties = []
        for p in pou["properties"]:
            p_comment = extract_comment(p["declaration"])
            p_meta = _extract_declaration_meta(p["declaration"])
            properties.append(PropertyDoc(
                name=p["name"],
                return_type=_extract_property_type(p["declaration"]),
                comment=p_comment,
                visibility=p_meta["visibility"],
                has_get=p.get("get") is not None,
                has_set=p.get("set") is not None,
            ))

        objects.append(ObjectDoc(
            name=pou["name"],
            obj_type=pou["pou_type"],
            declaration=pou["declaration"],
            comment=comment,
            visibility=meta["visibility"],
            is_abstract=meta["is_abstract"],
            is_final=meta["is_final"],
            extends=meta["extends"],
            implements=meta["implements"],
            inputs=vars_["input"],
            outputs=vars_["output"],
            inout=vars_["inout"],
            variables=vars_["variable"],
            methods=methods,
            properties=properties,
            actions=[a["name"] for a in pou.get("actions", [])],
        ))

    for path in tcgvl_files:
        try:
            gvl = parse_tcgvl(path)
        except Exception:
            continue
        comment = extract_comment(gvl["declaration"])
        vars_ = _parse_variables(gvl["declaration"])
        objects.append(ObjectDoc(
            name=gvl["name"],
            obj_type="gvl",
            declaration=gvl["declaration"],
            comment=comment,
            variables=vars_["variable"],
        ))

    for path in tcdut_files:
        try:
            dut = parse_tcdut(path)
        except Exception:
            continue
        comment = extract_comment(dut["declaration"])
        decl_upper = dut["declaration"].upper()
        # TwinCAT enums use ( ... ) syntax, not an ENUM keyword.
        # Structs and unions use STRUCT/UNION keywords.
        obj_type = "struct" if ("STRUCT" in decl_upper or "UNION" in decl_upper) else "enum"
        vars_ = _parse_variables(dut["declaration"])
        objects.append(ObjectDoc(
            name=dut["name"],
            obj_type=obj_type,
            declaration=dut["declaration"],
            comment=comment,
            variables=vars_["variable"],
        ))

    # Compute back-references after all objects are assembled
    _compute_used_by(objects)

    return ProjectDoc(name=project.name, objects=objects)


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

_METHOD_RETURN_RE = re.compile(r"METHOD\s+\w+\s*:\s*(\w+)", re.IGNORECASE)
_PROP_RETURN_RE = re.compile(r"PROPERTY\s+\w+\s*:\s*(\w+)", re.IGNORECASE)
_EXTENDS_RE = re.compile(r"\bEXTENDS\s+([\w.]+)", re.IGNORECASE)
_IMPLEMENTS_RE = re.compile(r"\bIMPLEMENTS\s+([\w.,\s]+)", re.IGNORECASE)
_VISIBILITY_WORDS = {"PUBLIC", "PRIVATE", "PROTECTED", "INTERNAL"}

_KEYWORDS_UPPER = {
    "FUNCTION_BLOCK", "FUNCTION", "PROGRAM", "INTERFACE",
    "METHOD", "PROPERTY", "TYPE",
}


def _extract_return_type(declaration: str) -> str:
    m = _METHOD_RETURN_RE.search(declaration)
    return m.group(1) if m else ""


def _extract_property_type(declaration: str) -> str:
    m = _PROP_RETURN_RE.search(declaration)
    return m.group(1) if m else ""


def _extract_declaration_meta(declaration: str) -> dict:
    """Extract visibility, extends, implements, abstract, final from declaration."""
    for line in declaration.splitlines():
        words = line.strip().split()
        if not words:
            continue
        if words[0].upper() not in _KEYWORDS_UPPER:
            continue
        upper_words = {w.upper().rstrip(",") for w in words}

        visibility = next((w for w in words if w.upper() in _VISIBILITY_WORDS), "")
        is_abstract = "ABSTRACT" in upper_words
        is_final = "FINAL" in upper_words

        m_ext = _EXTENDS_RE.search(line)
        extends = m_ext.group(1) if m_ext else ""

        m_impl = _IMPLEMENTS_RE.search(line)
        implements: list[str] = []
        if m_impl:
            implements = [i.strip().rstrip(",") for i in m_impl.group(1).split(",") if i.strip()]

        return {
            "visibility": visibility,
            "is_abstract": is_abstract,
            "is_final": is_final,
            "extends": extends,
            "implements": implements,
        }
    return {
        "visibility": "", "is_abstract": False, "is_final": False,
        "extends": "", "implements": [],
    }
