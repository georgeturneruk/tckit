"""Unit tests for _doc_model.py — project doc model building."""

import pytest

from tckit.adapters.doc_generators._doc_model import (
    _extract_declaration_meta,
    _parse_enum_members,
    _parse_struct_fields,
    _parse_variables,
    build_project_doc,
)

FIXTURES_PATH = "tests/fixtures/sample_project"


def _objects(project):
    """Return the flat object list across every PLC project in the doc.

    ADR-0005 keys ProjectDoc by PLC project name; the sample fixture is a
    single-PLC sln so this collapses to a single PLC's objects in practice.
    """
    return [obj for plc in project.plcs.values() for obj in plc.objects]


# ---------------------------------------------------------------------------
# Variable parser
# ---------------------------------------------------------------------------


class TestParseVariables:
    def test_var_input_extracted(self):
        decl = "FUNCTION_BLOCK FB_Test\nVAR_INPUT\n    bEnable : BOOL;\n    nCount  : INT;\nEND_VAR"
        result = _parse_variables(decl)
        names = [v.name for v in result["input"]]
        assert "bEnable" in names
        assert "nCount" in names

    def test_var_output_extracted(self):
        decl = "FUNCTION_BLOCK FB_Test\nVAR_OUTPUT\n    bDone : BOOL;\nEND_VAR"
        result = _parse_variables(decl)
        assert result["output"][0].name == "bDone"
        assert result["output"][0].var_type == "BOOL"

    def test_inline_comment_captured(self):
        decl = "FUNCTION_BLOCK FB_Test\nVAR_INPUT\n    bEnable : BOOL; // Trigger input\nEND_VAR"
        result = _parse_variables(decl)
        assert result["input"][0].comment == "Trigger input"

    def test_array_type_captured(self):
        decl = "FUNCTION_BLOCK FB_Test\nVAR\n    aData : ARRAY[0..9] OF BOOL;\nEND_VAR"
        result = _parse_variables(decl)
        assert "ARRAY" in result["variable"][0].var_type

    def test_initial_value_not_in_type(self):
        decl = "FUNCTION_BLOCK FB_Test\nVAR\n    nCount : INT := 0;\nEND_VAR"
        result = _parse_variables(decl)
        # Type should be "INT", not "INT := 0"
        assert result["variable"][0].var_type == "INT"

    def test_multiple_var_blocks(self):
        decl = (
            "FUNCTION_BLOCK FB_Test\n"
            "VAR_INPUT\n    x : BOOL;\nEND_VAR\n"
            "VAR_OUTPUT\n    y : BOOL;\nEND_VAR\n"
            "VAR\n    z : INT;\nEND_VAR"
        )
        result = _parse_variables(decl)
        assert len(result["input"]) == 1
        assert len(result["output"]) == 1
        assert len(result["variable"]) == 1

    def test_underscore_prefixed_private_var(self):
        decl = "FUNCTION_BLOCK FB_Test\nVAR\n    _state : INT;\nEND_VAR"
        result = _parse_variables(decl)
        assert result["variable"][0].name == "_state"

    def test_no_vars_returns_empty_lists(self):
        decl = "FUNCTION_BLOCK FB_Empty"
        result = _parse_variables(decl)
        assert result["input"] == []
        assert result["output"] == []
        assert result["variable"] == []

    def test_var_global_extracted(self):
        decl = (
            "VAR_GLOBAL CONSTANT\n"
            "    nMaxRetries : INT := 3;\n"
            "    sName : STRING := 'TcKit';\n"
            "END_VAR"
        )
        result = _parse_variables(decl)
        names = [v.name for v in result["variable"]]
        assert "nMaxRetries" in names
        assert "sName" in names

    def test_default_value_captured(self):
        decl = "VAR\n    nCount : INT := 42;\nEND_VAR"
        result = _parse_variables(decl)
        assert result["variable"][0].default_value == "42"

    def test_inline_block_comment_captured(self):
        decl = "VAR\n    fTimeout : LREAL := 5.0; (* seconds *)\nEND_VAR"
        result = _parse_variables(decl)
        v = result["variable"][0]
        assert v.comment == "seconds"
        assert v.default_value == "5.0"

    def test_block_comment_on_next_line_not_attributed_to_var_above(self):
        # Regression: previously `\s*` between `;` and the trailing block
        # comment crossed newlines, so a `(* ... *)` preceding the NEXT
        # variable was wrongly attached to the variable above.
        decl = (
            "VAR_GLOBAL\n"
            "    a : INT := 1;\n"
            "    (* doc for b, NOT for a *)\n"
            "    b : INT := 2;\n"
            "END_VAR"
        )
        result = _parse_variables(decl)
        by_name = {v.name: v for v in result["variable"]}
        assert by_name["a"].comment == ""
        assert by_name["b"].comment == ""  # We don't yet attach preceding comments either


# ---------------------------------------------------------------------------
# Struct field parser
# ---------------------------------------------------------------------------


class TestParseStructFields:
    def test_struct_fields_extracted(self):
        decl = (
            "TYPE ST_Config :\n"
            "STRUCT\n"
            "    nMaxRetries : INT := 3;\n"
            "    fTimeout    : LREAL := 5.0; (* seconds *)\n"
            "    bEnabled    : BOOL := TRUE;\n"
            "END_STRUCT\n"
            "END_TYPE"
        )
        fields = _parse_struct_fields(decl)
        names = [f.name for f in fields]
        assert names == ["nMaxRetries", "fTimeout", "bEnabled"]

    def test_struct_field_defaults_captured(self):
        decl = (
            "TYPE ST_Config :\nSTRUCT\n"
            "    nMaxRetries : INT := 3;\n"
            "END_STRUCT\nEND_TYPE"
        )
        fields = _parse_struct_fields(decl)
        assert fields[0].default_value == "3"

    def test_struct_field_inline_block_comment(self):
        decl = (
            "TYPE ST_Config :\nSTRUCT\n"
            "    fTimeout : LREAL := 5.0; (* seconds *)\n"
            "END_STRUCT\nEND_TYPE"
        )
        fields = _parse_struct_fields(decl)
        assert fields[0].comment == "seconds"

    def test_union_fields_extracted(self):
        decl = (
            "TYPE U_Data :\nUNION\n"
            "    asBytes : ARRAY[0..3] OF BYTE;\n"
            "    nWord   : DWORD;\n"
            "END_UNION\nEND_TYPE"
        )
        fields = _parse_struct_fields(decl)
        names = [f.name for f in fields]
        assert "asBytes" in names
        assert "nWord" in names

    def test_empty_struct_returns_empty_list(self):
        decl = "TYPE ST_Empty :\nSTRUCT\nEND_STRUCT\nEND_TYPE"
        assert _parse_struct_fields(decl) == []


# ---------------------------------------------------------------------------
# Enum member parser
# ---------------------------------------------------------------------------


class TestParseEnumMembers:
    def test_enum_members_extracted(self):
        decl = (
            "TYPE E_State :\n"
            "(\n"
            "    Idle := 0,\n"
            "    Running := 1,\n"
            "    Error := 2\n"
            ");\nEND_TYPE"
        )
        members = _parse_enum_members(decl)
        names = [m.name for m in members]
        assert names == ["Idle", "Running", "Error"]

    def test_enum_member_values_captured(self):
        decl = (
            "TYPE E_State :\n(\n"
            "    Idle := 0,\n"
            "    Running := 1\n"
            ");\nEND_TYPE"
        )
        members = _parse_enum_members(decl)
        assert members[0].var_type == "0"
        assert members[1].var_type == "1"

    def test_enum_members_without_explicit_values(self):
        decl = "TYPE E_X :\n(\n    A,\n    B,\n    C\n);\nEND_TYPE"
        members = _parse_enum_members(decl)
        assert [m.name for m in members] == ["A", "B", "C"]
        assert all(m.var_type == "" for m in members)

    def test_non_enum_returns_empty_list(self):
        # A struct should not produce enum members
        decl = "TYPE ST_X :\nSTRUCT\n    x : INT;\nEND_STRUCT\nEND_TYPE"
        assert _parse_enum_members(decl) == []


# ---------------------------------------------------------------------------
# Declaration meta extraction
# ---------------------------------------------------------------------------


class TestExtractDeclarationMeta:
    def test_no_modifiers(self):
        meta = _extract_declaration_meta("FUNCTION_BLOCK FB_Test")
        assert meta["visibility"] == ""
        assert meta["is_abstract"] is False
        assert meta["extends"] == ""
        assert meta["implements"] == []

    def test_public_visibility(self):
        meta = _extract_declaration_meta("FUNCTION_BLOCK PUBLIC TcoTask")
        assert meta["visibility"] == "PUBLIC"

    def test_private_visibility(self):
        meta = _extract_declaration_meta("METHOD PRIVATE AutoRestore")
        assert meta["visibility"] == "PRIVATE"

    def test_protected_visibility(self):
        meta = _extract_declaration_meta("METHOD PROTECTED Step : BOOL")
        assert meta["visibility"] == "PROTECTED"

    def test_abstract_modifier(self):
        meta = _extract_declaration_meta("FUNCTION_BLOCK PUBLIC ABSTRACT TcoTask")
        assert meta["is_abstract"] is True

    def test_final_modifier(self):
        meta = _extract_declaration_meta("METHOD PROTECTED FINAL CompleteStep")
        assert meta["is_final"] is True

    def test_extends(self):
        meta = _extract_declaration_meta("FUNCTION_BLOCK TcoTask EXTENDS TcoObject")
        assert meta["extends"] == "TcoObject"

    def test_implements_single(self):
        meta = _extract_declaration_meta("FUNCTION_BLOCK TcoTask IMPLEMENTS ITcoTask")
        assert "ITcoTask" in meta["implements"]

    def test_implements_multiple(self):
        meta = _extract_declaration_meta(
            "FUNCTION_BLOCK TcoTask EXTENDS TcoObject IMPLEMENTS ITcoTask, ITcoTaskStatus"
        )
        assert meta["extends"] == "TcoObject"
        assert set(meta["implements"]) == {"ITcoTask", "ITcoTaskStatus"}

    def test_extends_and_implements(self):
        meta = _extract_declaration_meta(
            "FUNCTION_BLOCK PUBLIC ABSTRACT TcoObject EXTENDS TcoParent IMPLEMENTS IBase, IExtra"
        )
        assert meta["visibility"] == "PUBLIC"
        assert meta["is_abstract"] is True
        assert meta["extends"] == "TcoParent"
        assert len(meta["implements"]) == 2

    def test_comment_line_ignored(self):
        # Preamble comment lines should not be treated as declaration
        meta = _extract_declaration_meta(
            "// FUNCTION_BLOCK in a comment\nFUNCTION_BLOCK FB_Real"
        )
        assert meta["visibility"] == ""  # no modifier on FB_Real line

    def test_method_with_return_type(self):
        meta = _extract_declaration_meta("METHOD PUBLIC Execute : BOOL")
        assert meta["visibility"] == "PUBLIC"


# ---------------------------------------------------------------------------
# Full project doc build (integration against fixtures)
# ---------------------------------------------------------------------------


class TestBuildProjectDoc:
    def test_finds_all_objects(self):
        project = build_project_doc(FIXTURES_PATH)
        names = [o.name for o in _objects(project)]
        assert "FB_Example" in names
        assert "GVL_Params" in names
        assert "ST_ExampleConfig" in names
        assert "E_ExampleState" in names

    def test_fb_has_description(self):
        project = build_project_doc(FIXTURES_PATH)
        fb = next(o for o in _objects(project) if o.name == "FB_Example")
        assert "TcKit" in fb.comment.description

    def test_fb_inputs_extracted(self):
        project = build_project_doc(FIXTURES_PATH)
        fb = next(o for o in _objects(project) if o.name == "FB_Example")
        input_names = [v.name for v in fb.inputs]
        assert "bEnable" in input_names
        assert "nSetpoint" in input_names

    def test_fb_input_descriptions_from_params(self):
        project = build_project_doc(FIXTURES_PATH)
        fb = next(o for o in _objects(project) if o.name == "FB_Example")
        enable = next(v for v in fb.inputs if v.name == "bEnable")
        assert enable.comment != ""  # enriched from :param bEnable:

    def test_fb_methods_extracted(self):
        project = build_project_doc(FIXTURES_PATH)
        fb = next(o for o in _objects(project) if o.name == "FB_Example")
        method_names = [m.name for m in fb.methods]
        assert "Execute" in method_names
        assert "Reset" in method_names

    def test_method_has_return_type(self):
        project = build_project_doc(FIXTURES_PATH)
        fb = next(o for o in _objects(project) if o.name == "FB_Example")
        execute = next(m for m in fb.methods if m.name == "Execute")
        assert execute.return_type == "BOOL"

    def test_method_has_description(self):
        project = build_project_doc(FIXTURES_PATH)
        fb = next(o for o in _objects(project) if o.name == "FB_Example")
        execute = next(m for m in fb.methods if m.name == "Execute")
        assert execute.comment.description != ""

    def test_fb_property_extracted(self):
        project = build_project_doc(FIXTURES_PATH)
        fb = next(o for o in _objects(project) if o.name == "FB_Example")
        prop_names = [p.name for p in fb.properties]
        assert "ErrorId" in prop_names

    def test_property_has_get_set(self):
        project = build_project_doc(FIXTURES_PATH)
        fb = next(o for o in _objects(project) if o.name == "FB_Example")
        errorid = next(p for p in fb.properties if p.name == "ErrorId")
        assert errorid.has_get is True
        assert errorid.has_set is True

    def test_gvl_type(self):
        project = build_project_doc(FIXTURES_PATH)
        gvl = next(o for o in _objects(project) if o.name == "GVL_Params")
        assert gvl.obj_type == "gvl"

    def test_struct_type(self):
        project = build_project_doc(FIXTURES_PATH)
        st = next(o for o in _objects(project) if o.name == "ST_ExampleConfig")
        assert st.obj_type == "struct"

    def test_enum_type(self):
        project = build_project_doc(FIXTURES_PATH)
        e = next(o for o in _objects(project) if o.name == "E_ExampleState")
        assert e.obj_type == "enum"

    def test_project_name_from_directory(self):
        project = build_project_doc(FIXTURES_PATH)
        assert project.name == "sample_project"

    def test_empty_project_raises(self):
        with pytest.raises(ValueError, match="No TwinCAT source files"):
            build_project_doc("/tmp/nonexistent_project")

    def test_gvl_variables_extracted(self):
        project = build_project_doc(FIXTURES_PATH)
        gvl = next(o for o in _objects(project) if o.name == "GVL_Params")
        names = [v.name for v in gvl.variables]
        assert "nMaxRetries" in names
        assert "fTimeout" in names
        assert "sProjectName" in names

    def test_gvl_default_value(self):
        project = build_project_doc(FIXTURES_PATH)
        gvl = next(o for o in _objects(project) if o.name == "GVL_Params")
        max_retries = next(v for v in gvl.variables if v.name == "nMaxRetries")
        assert max_retries.default_value == "3"

    def test_gvl_inline_block_comment(self):
        project = build_project_doc(FIXTURES_PATH)
        gvl = next(o for o in _objects(project) if o.name == "GVL_Params")
        timeout = next(v for v in gvl.variables if v.name == "fTimeout")
        assert timeout.comment == "seconds"

    def test_struct_fields_extracted(self):
        project = build_project_doc(FIXTURES_PATH)
        st = next(o for o in _objects(project) if o.name == "ST_ExampleConfig")
        names = [v.name for v in st.variables]
        assert "nMaxRetries" in names
        assert "fTimeout" in names
        assert "sDescription" in names
        assert "bEnabled" in names

    def test_enum_members_extracted(self):
        project = build_project_doc(FIXTURES_PATH)
        e = next(o for o in _objects(project) if o.name == "E_ExampleState")
        names = [v.name for v in e.variables]
        assert names == ["Idle", "Running", "Error"]
        values = [v.var_type for v in e.variables]
        assert values == ["0", "1", "2"]


class TestInterfaceDiscovery:
    """`.TcIO` is a separate extension that some projects (e.g. TcUnit) use for
    interfaces. parse_tcpou already handles `<Itf>` roots, so the bug was a
    missing glob in `_build_plc_doc_from_root`.
    """

    def test_tcio_file_is_picked_up(self, tmp_path):
        project_dir = tmp_path / "tcio_project"
        project_dir.mkdir()
        (project_dir / "I_Sample.TcIO").write_text(
            '<?xml version="1.0"?>'
            '<TcPlcObject>'
            '<Itf Name="I_Sample">'
            '<Declaration><![CDATA[INTERFACE I_Sample\n]]></Declaration>'
            '<Method Name="DoStuff">'
            '<Declaration><![CDATA[METHOD DoStuff : BOOL\n]]></Declaration>'
            '</Method>'
            '</Itf>'
            '</TcPlcObject>',
            encoding="utf-8",
        )
        project = build_project_doc(str(project_dir))
        names = [o.name for o in _objects(project)]
        assert "I_Sample" in names
        i = next(o for o in _objects(project) if o.name == "I_Sample")
        assert i.obj_type == "interface"
        assert [m.name for m in i.methods] == ["DoStuff"]
