"""_doc_model — build a structured documentation model from a TwinCAT project.

Orchestrates tc_file_parser (structure) and _comment_extractor (comments)
into a ProjectDoc tree that templates can render directly.
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from pathlib import Path

from tckit.adapters.doc_generators._comment_extractor import extract_comment
from tckit.ports.types import CommentDoc
from tckit.utils.tc_file_parser import (
    parse_tcdut,
    parse_tcgvl,
    parse_tcpou,
)

# ---------------------------------------------------------------------------
# Doc model dataclasses
# ---------------------------------------------------------------------------


@dataclass
class VariableDoc:
    name: str
    var_type: str
    comment: str = ""
    default_value: str = ""


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
    plc_name: str = ""            # PLC project (.plcproj stem) that owns this object
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
class PLCDoc:
    """One PLC project's worth of documentation.

    Attribute names mirror the previous ProjectDoc shape (``name``,
    ``objects``) so per-PLC templates can take a PLCDoc as ``project``
    in the render context without modification.
    """

    name: str
    plcproj_path: str
    objects: list[ObjectDoc] = field(default_factory=list)


@dataclass
class ProjectDoc:
    """A whole solution's documentation, keyed by PLC-project name.

    Multi-project sln support per ADR-0005. Single-project solutions
    produce a one-entry ``plcs`` dict.
    """

    name: str
    plcs: dict[str, PLCDoc] = field(default_factory=dict)


# ---------------------------------------------------------------------------
# Variable block parser
# ---------------------------------------------------------------------------

_VAR_BLOCK_RE = re.compile(
    r"VAR(?P<kind>_INPUT|_OUTPUT|_IN_OUT|_STAT|_TEMP|_GLOBAL)?\b.*?END_VAR",
    re.DOTALL | re.IGNORECASE,
)
# Variable line regex. Every horizontal-whitespace match uses [ \t]* (not \s*)
# to keep parsing strictly line-scoped. Otherwise the trailing-comment branch
# would happily cross a newline and pick up the `(* ... *)` block comment
# preceding the NEXT variable, attributing it to the variable above.
_VAR_LINE_RE = re.compile(
    r"^[ \t]*(?P<name>[A-Za-z_]\w*)[ \t]*:[ \t]*(?P<type>[^:;=\n]+?)"
    r"[ \t]*(?::=[ \t]*(?P<default>[^;\n]+?))?[ \t]*;"
    r"(?:[ \t]*(?://[ \t]*(?P<comment>.*)|\(\*[ \t]*(?P<block_comment>.*?)[ \t]*\*\)))?",
    re.MULTILINE,
)
_STRUCT_BLOCK_RE = re.compile(
    r"\bSTRUCT\b(?P<body>.*?)\bEND_STRUCT\b",
    re.DOTALL | re.IGNORECASE,
)
_UNION_BLOCK_RE = re.compile(
    r"\bUNION\b(?P<body>.*?)\bEND_UNION\b",
    re.DOTALL | re.IGNORECASE,
)
_ENUM_BLOCK_RE = re.compile(
    r"TYPE\s+\w+\s*:\s*\((?P<body>.*?)\)\s*;?\s*END_TYPE",
    re.DOTALL | re.IGNORECASE,
)
_ENUM_MEMBER_RE = re.compile(
    r"^\s*(?P<name>[A-Za-z_]\w*)\s*"
    r"(?::=\s*(?P<value>[^,/\n]+?))?\s*"
    r"(?:,)?\s*"
    r"(?://\s*(?P<comment>.*))?\s*$",
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
            if name.upper() in ("VAR", "END_VAR", "VAR_GLOBAL", "CONSTANT", "PERSISTENT"):
                continue
            line_comment = var_match.group("comment") or var_match.group("block_comment") or ""
            result[category].append(VariableDoc(
                name=name,
                var_type=var_match.group("type").strip(),
                comment=line_comment.strip(),
                default_value=(var_match.group("default") or "").strip(),
            ))
    return result


def _parse_struct_fields(declaration: str) -> list[VariableDoc]:
    """Extract field list from a STRUCT or UNION body.

    TwinCAT structs use ``STRUCT ... END_STRUCT`` rather than ``VAR ... END_VAR``,
    so the main variable-block parser misses them. This helper scans both
    STRUCT and UNION bodies and returns a flat list of fields.
    """
    fields: list[VariableDoc] = []
    for block_re in (_STRUCT_BLOCK_RE, _UNION_BLOCK_RE):
        for block_match in block_re.finditer(declaration):
            body = block_match.group("body")
            for var_match in _VAR_LINE_RE.finditer(body):
                name = var_match.group("name")
                if name.upper() in ("VAR", "END_VAR", "STRUCT", "END_STRUCT",
                                    "UNION", "END_UNION", "CONSTANT", "PERSISTENT"):
                    continue
                line_comment = (
                    var_match.group("comment")
                    or var_match.group("block_comment")
                    or ""
                )
                fields.append(VariableDoc(
                    name=name,
                    var_type=var_match.group("type").strip(),
                    comment=line_comment.strip(),
                    default_value=(var_match.group("default") or "").strip(),
                ))
    return fields


def _parse_enum_members(declaration: str) -> list[VariableDoc]:
    """Extract members from a TwinCAT enum declaration.

    Enums use ``TYPE E_X : ( Name := value, ... ); END_TYPE`` syntax. Each
    member is reported as a VariableDoc with the literal value stored in
    ``var_type`` so the existing var_table renderer can be reused with a
    relabelled column.
    """
    members: list[VariableDoc] = []
    block_match = _ENUM_BLOCK_RE.search(declaration)
    if not block_match:
        return members
    body = block_match.group("body")
    for member_match in _ENUM_MEMBER_RE.finditer(body):
        name = member_match.group("name")
        if not name:
            continue
        members.append(VariableDoc(
            name=name,
            var_type=(member_match.group("value") or "").strip(),
            comment=(member_match.group("comment") or "").strip(),
        ))
    return members


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
    """Build a full ProjectDoc from a TwinCAT solution directory.

    Discovers every ``.plcproj`` under ``project_path`` and builds one
    ``PLCDoc`` per PLC project. Used-by cross-references are scoped within
    a single PLC project so a type in one PLC project never claims to be
    "used by" a type in another (see ADR-0005).

    Args:
        project_path: Absolute path to the solution directory.

    Raises:
        ValueError: If no TwinCAT source files are found anywhere under
            ``project_path``.
    """
    project = Path(project_path).resolve()
    plcproj_paths = sorted(project.rglob("*.plcproj"))

    plcs: dict[str, PLCDoc] = {}
    total_objects = 0
    if plcproj_paths:
        for plcproj in plcproj_paths:
            plc_doc = _build_plc_doc(plcproj)
            plcs[plc_doc.name] = plc_doc
            total_objects += len(plc_doc.objects)
    else:
        # No .plcproj anywhere — fall back to a single anonymous PLC built
        # straight from the project directory. Preserves the legacy
        # "loose project directory" use case (and some tests rely on it).
        plc_doc = _build_plc_doc_from_root(project, plc_name=project.name)
        if plc_doc.objects:
            plcs[plc_doc.name] = plc_doc
            total_objects += len(plc_doc.objects)

    if total_objects == 0:
        raise ValueError(f"No TwinCAT source files found in {project_path}")

    return ProjectDoc(name=project.name, plcs=plcs)


def _build_plc_doc(plcproj: Path) -> PLCDoc:
    """Build a PLCDoc by walking a single .plcproj's sibling tree."""
    plc_root = plcproj.parent
    plc_name = plcproj.stem
    return _build_plc_doc_from_root(plc_root, plc_name=plc_name, plcproj_path=str(plcproj))


def _build_plc_doc_from_root(
    root: Path, *, plc_name: str, plcproj_path: str = ""
) -> PLCDoc:
    """Walk ``root`` for TwinCAT source files and assemble a PLCDoc."""
    # .TcIO is the dedicated interface file extension used by some projects
    # (e.g. TcUnit). parse_tcpou already handles <Itf> roots, so we glob both.
    tcpou_files = sorted(
        list(root.glob("**/*.TcPOU")) + list(root.glob("**/*.TcIO"))
    )
    tcgvl_files = sorted(root.glob("**/*.TcGVL"))
    tcdut_files = sorted(root.glob("**/*.TcDUT"))

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
            plc_name=plc_name,
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
            plc_name=plc_name,
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
        is_struct = "STRUCT" in decl_upper or "UNION" in decl_upper
        obj_type = "struct" if is_struct else "enum"
        if is_struct:
            variables = _parse_struct_fields(dut["declaration"])
        else:
            variables = _parse_enum_members(dut["declaration"])
        objects.append(ObjectDoc(
            name=dut["name"],
            obj_type=obj_type,
            declaration=dut["declaration"],
            comment=comment,
            plc_name=plc_name,
            variables=variables,
        ))

    # Used-by cross-references are scoped within this PLC project. See
    # ADR-0005: a type in one PLC project shouldn't claim to be "used by"
    # a type in another, even when their names happen to coincide.
    _compute_used_by(objects)

    return PLCDoc(name=plc_name, plcproj_path=plcproj_path, objects=objects)


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
