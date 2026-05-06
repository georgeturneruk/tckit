"""Unit tests for _comment_extractor.py — comment style detection and parsing."""

import pytest

from tckit.adapters.doc_generators._comment_extractor import (
    _detect_style,
    _extract_preamble,
    extract_comment,
)


# ---------------------------------------------------------------------------
# Preamble extraction
# ---------------------------------------------------------------------------


class TestExtractPreamble:
    def test_stops_at_function_block(self):
        decl = "// :Description: Some FB\nFUNCTION_BLOCK FB_Test\nVAR_INPUT\n    x : BOOL;\nEND_VAR"
        assert "FUNCTION_BLOCK" not in _extract_preamble(decl)
        assert ":Description:" in _extract_preamble(decl)

    def test_stops_at_method(self):
        decl = "// :Description: A method\nMETHOD Execute : BOOL\nVAR_INPUT\n    x : BOOL;\nEND_VAR"
        assert "METHOD" not in _extract_preamble(decl)

    def test_keyword_inside_comment_not_boundary(self):
        # "FUNCTION_BLOCK" appears in description text — should NOT be treated as boundary
        decl = "// :Description: Wraps a FUNCTION_BLOCK pattern\nFUNCTION_BLOCK FB_Test"
        preamble = _extract_preamble(decl)
        assert "Wraps a FUNCTION_BLOCK pattern" in preamble
        assert preamble.count("FUNCTION_BLOCK") == 1  # only in the comment, not the keyword line

    def test_keyword_inside_xml_comment_not_boundary(self):
        decl = "(*~\n<docu><summary>Contains a METHOD call</summary></docu>\n~*)\nFUNCTION_BLOCK FB_Test"
        preamble = _extract_preamble(decl)
        assert "METHOD call" in preamble

    def test_empty_declaration(self):
        assert _extract_preamble("") == ""

    def test_no_keyword_returns_full_text(self):
        # A GVL declaration has no keyword at line start (VAR_GLOBAL is not in keywords)
        decl = "// :Description: Some globals\nVAR_GLOBAL\n    x : BOOL;\nEND_VAR"
        preamble = _extract_preamble(decl)
        assert ":Description:" in preamble


# ---------------------------------------------------------------------------
# Style detection
# ---------------------------------------------------------------------------


class TestDetectStyle:
    def test_detects_xml_docu(self):
        assert _detect_style("(*~\n<docu><summary>Test</summary></docu>\n~*)") == "xml_docu"

    def test_detects_block_rst(self):
        assert _detect_style("(* :Description: Some FB *)") == "block_rst"

    def test_detects_line_rst(self):
        assert _detect_style("// :Description: Some FB\n// :param x: Input") == "line_rst"

    def test_plain_comment(self):
        assert _detect_style("// just a plain comment") == "line_rst"

    def test_empty_is_plain(self):
        assert _detect_style("") == "plain"

    def test_attribute_pragma_only_is_plain(self):
        assert _detect_style("{attribute 'hide'}") == "plain"


# ---------------------------------------------------------------------------
# RST line comment style
# ---------------------------------------------------------------------------


class TestLineRst:
    def test_basic_description(self):
        decl = "// :Description: Example function block.\nFUNCTION_BLOCK FB_Test"
        result = extract_comment(decl)
        assert result.description == "Example function block."

    def test_description_with_function_word(self):
        # Regression: "function" in description must not truncate it
        decl = "// :Description: Example function block for TcKit parser validation.\nFUNCTION_BLOCK FB_Test"
        result = extract_comment(decl)
        assert result.description == "Example function block for TcKit parser validation."

    def test_params_extracted(self):
        decl = "// :param bEnable: Rising edge starts the operation\n// :param nSetpoint: Target value\nFUNCTION_BLOCK FB_Test"
        result = extract_comment(decl)
        assert result.params == {"bEnable": "Rising edge starts the operation", "nSetpoint": "Target value"}

    def test_returns_extracted(self):
        decl = "// :returns: TRUE when operation completes\nMETHOD Execute : BOOL"
        result = extract_comment(decl)
        assert result.returns == "TRUE when operation completes"

    def test_remarks_extracted(self):
        decl = "// :remarks: Only call once per cycle\nMETHOD Execute : BOOL"
        result = extract_comment(decl)
        assert result.remarks == "Only call once per cycle"

    def test_plain_line_comment_becomes_description(self):
        decl = "// This is a plain comment\nFUNCTION_BLOCK FB_Test"
        result = extract_comment(decl)
        assert result.description == "This is a plain comment"

    def test_attribute_pragma_not_in_description(self):
        decl = "{attribute 'hide'}\n// :Description: A hidden FB\nFUNCTION_BLOCK FB_Test"
        result = extract_comment(decl)
        assert "{attribute" not in result.description
        assert result.description == "A hidden FB"

    def test_multiple_attribute_pragmas(self):
        decl = "{attribute clr [ReadOnly()]}\n{attribute 'monitoring' := 'variable'}\n// :Description: Read-only value\nPROPERTY MyProp : BOOL"
        result = extract_comment(decl)
        assert result.description == "Read-only value"

    def test_no_comment_returns_empty(self):
        decl = "FUNCTION_BLOCK FB_Test\nVAR_INPUT\n    x : BOOL;\nEND_VAR"
        result = extract_comment(decl)
        assert result.description == ""
        assert result.params == {}
        assert result.returns == ""


# ---------------------------------------------------------------------------
# XML <docu> style (TcOpen / Beckhoff TE1030)
# ---------------------------------------------------------------------------


class TestXmlDocu:
    def test_summary_extracted(self):
        decl = "(*~\n<docu><summary>Provides basic task execution.</summary></docu>\n~*)\nFUNCTION_BLOCK TcoTask"
        result = extract_comment(decl)
        assert "basic task execution" in result.description

    def test_param_with_name_attribute(self):
        decl = '(*~\n<docu><param name="bEnable">Enables the task</param></docu>\n~*)\nMETHOD Execute'
        result = extract_comment(decl)
        assert result.params.get("bEnable") == "Enables the task"

    def test_returns_extracted(self):
        decl = "(*~\n<docu><returns>TRUE when done</returns></docu>\n~*)\nMETHOD Execute : BOOL"
        result = extract_comment(decl)
        assert result.returns == "TRUE when done"

    def test_nested_para_tags_stripped(self):
        decl = "(*~\n<docu><summary><para>Task execution via <see cref=\"ITcoTask\"/>.</para></summary></docu>\n~*)\nFUNCTION_BLOCK TcoTask"
        result = extract_comment(decl)
        assert "<para>" not in result.description
        assert "<see" not in result.description
        assert "Task execution" in result.description

    def test_remarks_extracted(self):
        decl = "(*~\n<docu><summary>Brief.</summary><remarks>Only call cyclically.</remarks></docu>\n~*)\nFUNCTION_BLOCK FB_Test"
        result = extract_comment(decl)
        assert result.remarks == "Only call cyclically."


# ---------------------------------------------------------------------------
# Block RST style
# ---------------------------------------------------------------------------


class TestBlockRst:
    def test_basic_description(self):
        decl = "(* :Description: A block-commented FB\n:param x: Input value\n*)\nFUNCTION_BLOCK FB_Test"
        result = extract_comment(decl)
        assert "block-commented" in result.description

    def test_param_extracted(self):
        decl = "(* :param bEnable: Trigger input\n*)\nMETHOD Execute : BOOL"
        result = extract_comment(decl)
        assert result.params.get("bEnable") == "Trigger input"


# ---------------------------------------------------------------------------
# Edge cases
# ---------------------------------------------------------------------------


class TestEdgeCases:
    def test_method_with_public_modifier(self):
        decl = "// :Description: Reset state\nMETHOD PUBLIC Reset"
        result = extract_comment(decl)
        assert result.description == "Reset state"

    def test_function_block_public_abstract(self):
        decl = "// :Description: Abstract base\nFUNCTION_BLOCK PUBLIC ABSTRACT TcoObject"
        result = extract_comment(decl)
        assert result.description == "Abstract base"

    def test_description_with_interface_keyword(self):
        # "INTERFACE" in description must not truncate
        decl = "// :Description: Implements the INTERFACE pattern\nFUNCTION_BLOCK FB_Test"
        result = extract_comment(decl)
        assert result.description == "Implements the INTERFACE pattern"

    def test_summary_alias(self):
        # :Summary: should also map to description
        decl = "// :Summary: Brief summary here\nFUNCTION_BLOCK FB_Test"
        result = extract_comment(decl)
        assert result.description == "Brief summary here"

    def test_return_alias(self):
        decl = "// :return: The result value\nMETHOD Execute : BOOL"
        result = extract_comment(decl)
        assert result.returns == "The result value"
