"""Doc-generator multi-project sln tests (ADR-0005).

Verifies that the HtmlGenerator and MarkdownGenerator emit one sub-tree per
``.plcproj`` and that ``used by`` cross-references stay within a single PLC
project (no cross-PLC bleed).
"""

from __future__ import annotations

from pathlib import Path

import pytest

from tckit.adapters.doc_generators._doc_model import build_project_doc
from tckit.adapters.doc_generators.html_generator import HtmlGenerator
from tckit.adapters.doc_generators.markdown_generator import MarkdownGenerator


def test_build_project_doc_emits_one_plcdoc_per_plcproj(
    multi_project_sln_path: Path,
) -> None:
    project = build_project_doc(str(multi_project_sln_path))
    assert set(project.plcs.keys()) == {"Library", "Tests"}


def test_objectdoc_carries_owning_plc_name(multi_project_sln_path: Path) -> None:
    project = build_project_doc(str(multi_project_sln_path))
    lib_objects = project.plcs["Library"].objects
    test_objects = project.plcs["Tests"].objects
    assert all(o.plc_name == "Library" for o in lib_objects)
    assert all(o.plc_name == "Tests" for o in test_objects)


def test_used_by_scoped_within_plc(multi_project_sln_path: Path) -> None:
    """FB_FilterTests references FB_Filter, but they live in different PLCs.

    The library's FB_Filter must NOT have FB_FilterTests in its used_by list:
    cross-PLC references are deliberately suppressed in the doc model (the
    library and the tests project see each other only via a linked-library
    reference, which the doc model doesn't track).
    """
    project = build_project_doc(str(multi_project_sln_path))
    fb_filter = next(
        o for o in project.plcs["Library"].objects if o.name == "FB_Filter"
    )
    assert "FB_FilterTests" not in fb_filter.used_by


def test_used_by_within_same_plc_still_works(
    multi_project_sln_path: Path,
) -> None:
    """A within-PLC reference (FB_Filter -> E_State, both in Library) is recorded."""
    project = build_project_doc(str(multi_project_sln_path))
    e_state_lib = next(
        o for o in project.plcs["Library"].objects if o.name == "E_State"
    )
    assert "FB_Filter" in e_state_lib.used_by


# ---------------------------------------------------------------------------
# HtmlGenerator output layout
# ---------------------------------------------------------------------------


@pytest.fixture()
def html_output(multi_project_sln_path: Path, tmp_path: Path) -> Path:
    out = tmp_path / "html"
    result = HtmlGenerator().generate(str(multi_project_sln_path), str(out))
    assert result.success, result.error
    return out


def test_html_top_level_index_exists(html_output: Path) -> None:
    assert (html_output / "index.html").exists()


def test_html_top_level_index_links_to_each_plc(html_output: Path) -> None:
    text = (html_output / "index.html").read_text(encoding="utf-8")
    assert "Library/index.html" in text
    assert "Tests/index.html" in text


def test_html_per_plc_subtree_exists(html_output: Path) -> None:
    assert (html_output / "Library" / "index.html").exists()
    assert (html_output / "Library" / "FB_Filter.html").exists()
    assert (html_output / "Tests" / "index.html").exists()
    assert (html_output / "Tests" / "FB_FilterTests.html").exists()


def test_html_duplicated_symbol_lands_in_each_plc(html_output: Path) -> None:
    """E_State exists in both PLCs and gets its own page in each sub-tree."""
    assert (html_output / "Library" / "E_State.html").exists()
    assert (html_output / "Tests" / "E_State.html").exists()


def test_html_per_plc_search_index_exists(html_output: Path) -> None:
    assert (html_output / "Library" / "search-index.json").exists()
    assert (html_output / "Tests" / "search-index.json").exists()


# ---------------------------------------------------------------------------
# MarkdownGenerator output layout
# ---------------------------------------------------------------------------


@pytest.fixture()
def md_output(multi_project_sln_path: Path, tmp_path: Path) -> Path:
    out = tmp_path / "md"
    result = MarkdownGenerator().generate(str(multi_project_sln_path), str(out))
    assert result.success, result.error
    return out


def test_md_top_level_index_exists(md_output: Path) -> None:
    assert (md_output / "index.md").exists()


def test_md_top_level_index_links_to_each_plc(md_output: Path) -> None:
    text = (md_output / "index.md").read_text(encoding="utf-8")
    assert "Library/index.md" in text
    assert "Tests/index.md" in text


def test_md_per_plc_subtree_exists(md_output: Path) -> None:
    assert (md_output / "Library" / "FB_Filter.md").exists()
    assert (md_output / "Tests" / "FB_FilterTests.md").exists()
