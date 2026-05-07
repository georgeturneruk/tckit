"""HTML content validation tests for HtmlGenerator.

Calls generate() once for the whole module (module-scoped fixture) then
asserts that the rendered HTML contains the right content for each kind
of object. BeautifulSoup is used to parse the HTML rather than regex.

These tests complement test_doc_model.py (which tests data extraction)
and test_html_generator.py (which tests status/file-existence). This file
is the end-to-end rendering layer.
"""

from __future__ import annotations

from pathlib import Path

import pytest
from bs4 import BeautifulSoup

from tckit.adapters.doc_generators.html_generator import HtmlGenerator

FIXTURES = "tests/fixtures/sample_project"


# ---------------------------------------------------------------------------
# Module-scoped fixtures — generate once, share across all tests
# ---------------------------------------------------------------------------


@pytest.fixture(scope="module")
def docs(tmp_path_factory: pytest.TempPathFactory) -> Path:
    output = tmp_path_factory.mktemp("html_docs")
    result = HtmlGenerator().generate(FIXTURES, str(output))
    assert result.success, f"generate() failed: {result.error}"
    return output


@pytest.fixture(scope="module")
def index_soup(docs: Path) -> BeautifulSoup:
    return BeautifulSoup((docs / "index.html").read_text(encoding="utf-8"), "html.parser")


@pytest.fixture(scope="module")
def fb_soup(docs: Path) -> BeautifulSoup:
    return BeautifulSoup((docs / "FB_Example.html").read_text(encoding="utf-8"), "html.parser")


@pytest.fixture(scope="module")
def gvl_soup(docs: Path) -> BeautifulSoup:
    return BeautifulSoup((docs / "GVL_Params.html").read_text(encoding="utf-8"), "html.parser")


@pytest.fixture(scope="module")
def struct_soup(docs: Path) -> BeautifulSoup:
    return BeautifulSoup((docs / "ST_ExampleConfig.html").read_text(encoding="utf-8"), "html.parser")


@pytest.fixture(scope="module")
def enum_soup(docs: Path) -> BeautifulSoup:
    return BeautifulSoup((docs / "E_ExampleState.html").read_text(encoding="utf-8"), "html.parser")


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


def page_text(soup: BeautifulSoup) -> str:
    return soup.get_text()


def has_heading(soup: BeautifulSoup, text: str) -> bool:
    for tag in soup.find_all(["h1", "h2", "h3"]):
        if text in tag.get_text():
            return True
    return False


# ---------------------------------------------------------------------------
# Group 1 — index.html (9 tests)
# ---------------------------------------------------------------------------


class TestIndex:
    def test_contains_project_name(self, index_soup: BeautifulSoup) -> None:
        h1 = index_soup.find("h1")
        assert h1 is not None
        assert "sample_project" in h1.get_text()

    def test_function_blocks_section(self, index_soup: BeautifulSoup) -> None:
        assert has_heading(index_soup, "Function Blocks")

    def test_fb_example_link(self, index_soup: BeautifulSoup) -> None:
        link = index_soup.find("a", href="FB_Example.html")
        assert link is not None

    def test_fb_example_description_snippet(self, index_soup: BeautifulSoup) -> None:
        assert "Example function block" in page_text(index_soup)

    def test_gvl_section(self, index_soup: BeautifulSoup) -> None:
        assert has_heading(index_soup, "Global Variable Lists")

    def test_struct_section(self, index_soup: BeautifulSoup) -> None:
        assert has_heading(index_soup, "Structures")

    def test_enum_section(self, index_soup: BeautifulSoup) -> None:
        assert has_heading(index_soup, "Enumerations")

    def test_type_badges_present(self, index_soup: BeautifulSoup) -> None:
        badges = index_soup.select("[class*=badge-fb]")
        assert len(badges) > 0

    def test_no_raw_rst_markers_in_table_cells(self, index_soup: BeautifulSoup) -> None:
        """RST markers must not appear in table description cells (only in <pre> is ok)."""
        # Clone soup to strip <pre> blocks (raw source) before checking cells
        import copy
        clean = copy.copy(index_soup)
        for pre in clean.find_all("pre"):
            pre.decompose()
        cell_text = " ".join(td.get_text() for td in clean.find_all("td"))
        assert ":Description:" not in cell_text
        assert ":param" not in cell_text


# ---------------------------------------------------------------------------
# Group 2 — FB_Example.html header (5 tests)
# ---------------------------------------------------------------------------


class TestFBHeader:
    def test_name_in_h1(self, fb_soup: BeautifulSoup) -> None:
        h1 = fb_soup.find("h1")
        assert h1 is not None
        assert "FB_Example" in h1.get_text()

    def test_type_badge_text(self, fb_soup: BeautifulSoup) -> None:
        assert "Function Block" in page_text(fb_soup)

    def test_description_present(self, fb_soup: BeautifulSoup) -> None:
        assert "Example function block for TcKit parser validation." in page_text(fb_soup)

    def test_no_actions_section(self, fb_soup: BeautifulSoup) -> None:
        """Fixture has no actions — Actions heading must not appear."""
        assert not has_heading(fb_soup, "Actions")

    def test_declaration_block_present(self, fb_soup: BeautifulSoup) -> None:
        pre_blocks = fb_soup.find_all("pre")
        pre_text = " ".join(p.get_text() for p in pre_blocks)
        assert "FUNCTION_BLOCK" in pre_text


# ---------------------------------------------------------------------------
# Group 3 — FB_Example.html variable tables (6 tests)
# ---------------------------------------------------------------------------


class TestFBVariables:
    def test_inputs_heading(self, fb_soup: BeautifulSoup) -> None:
        assert has_heading(fb_soup, "Inputs")

    def test_input_names_present(self, fb_soup: BeautifulSoup) -> None:
        code_texts = [c.get_text() for c in fb_soup.find_all("code")]
        assert "bEnable" in code_texts
        assert "nSetpoint" in code_texts

    def test_input_descriptions_from_params(self, fb_soup: BeautifulSoup) -> None:
        """Param descriptions from :param comments must appear in the inputs table."""
        assert "Rising edge starts the operation" in page_text(fb_soup)

    def test_outputs_heading(self, fb_soup: BeautifulSoup) -> None:
        assert has_heading(fb_soup, "Outputs")

    def test_output_names_present(self, fb_soup: BeautifulSoup) -> None:
        code_texts = [c.get_text() for c in fb_soup.find_all("code")]
        assert "bDone" in code_texts
        assert "bError" in code_texts
        assert "nErrorId" in code_texts

    def test_variables_section(self, fb_soup: BeautifulSoup) -> None:
        assert has_heading(fb_soup, "Variables")
        assert "eState" in page_text(fb_soup)


# ---------------------------------------------------------------------------
# Group 4 — FB_Example.html methods (6 tests)
# ---------------------------------------------------------------------------


class TestFBMethods:
    def test_methods_heading(self, fb_soup: BeautifulSoup) -> None:
        assert has_heading(fb_soup, "Methods")

    def test_execute_method_name(self, fb_soup: BeautifulSoup) -> None:
        # Find in item-card headers
        headers = fb_soup.select(".item-card-header")
        names = [h.get_text() for h in headers]
        assert any("Execute" in n for n in names)

    def test_execute_return_type(self, fb_soup: BeautifulSoup) -> None:
        assert ": BOOL" in page_text(fb_soup)

    def test_execute_description(self, fb_soup: BeautifulSoup) -> None:
        assert "Execute one cycle of the operation." in page_text(fb_soup)

    def test_execute_returns_doc(self, fb_soup: BeautifulSoup) -> None:
        assert "TRUE when the operation completes successfully" in page_text(fb_soup)

    def test_execute_body_in_pre(self, fb_soup: BeautifulSoup) -> None:
        """Method implementation body must appear in a <pre><code> block."""
        pre_text = " ".join(p.get_text() for p in fb_soup.find_all("pre"))
        # "bDone" appears in Execute's implementation body
        assert "bDone" in pre_text


# ---------------------------------------------------------------------------
# Group 5 — FB_Example.html properties (4 tests)
# ---------------------------------------------------------------------------


class TestFBProperties:
    def test_properties_heading(self, fb_soup: BeautifulSoup) -> None:
        assert has_heading(fb_soup, "Properties")

    def test_errorid_name(self, fb_soup: BeautifulSoup) -> None:
        headers = fb_soup.select(".item-card-header")
        assert any("ErrorId" in h.get_text() for h in headers)

    def test_errorid_get_badge(self, fb_soup: BeautifulSoup) -> None:
        get_badges = [
            el for el in fb_soup.find_all(class_=lambda c: c and "badge" in c)
            if "GET" in el.get_text()
        ]
        assert len(get_badges) > 0

    def test_errorid_set_badge(self, fb_soup: BeautifulSoup) -> None:
        set_badges = [
            el for el in fb_soup.find_all(class_=lambda c: c and "badge" in c)
            if "SET" in el.get_text()
        ]
        assert len(set_badges) > 0


# ---------------------------------------------------------------------------
# Group 6 — GVL, Struct, Enum type badges (3 tests)
# ---------------------------------------------------------------------------


class TestTypeBadges:
    def test_gvl_type_badge(self, gvl_soup: BeautifulSoup) -> None:
        assert "GVL" in page_text(gvl_soup)

    def test_struct_type_badge(self, struct_soup: BeautifulSoup) -> None:
        assert "Struct" in page_text(struct_soup)

    def test_enum_type_badge(self, enum_soup: BeautifulSoup) -> None:
        assert "Enum" in page_text(enum_soup)


# ---------------------------------------------------------------------------
# Group 7 — HTML safety (2 tests)
# ---------------------------------------------------------------------------


class TestHtmlSafety:
    def test_no_raw_rst_markers_in_rendered_content(self, docs: Path) -> None:
        """RST markers must not appear in rendered paragraphs or table cells.

        They may appear inside <pre><code> blocks (raw source display is intentional).
        """
        import copy
        for html_file in docs.glob("*.html"):
            soup = BeautifulSoup(html_file.read_text(encoding="utf-8"), "html.parser")
            # Remove raw source blocks before checking rendered content
            for pre in soup.find_all("pre"):
                pre.decompose()
            rendered = " ".join(
                el.get_text()
                for el in soup.find_all(["p", "td", "li", "h1", "h2", "h3"])
            )
            assert ":Description:" not in rendered, (
                f":Description: leaked into rendered content of {html_file.name}"
            )
            assert ":returns:" not in rendered, (
                f":returns: leaked into rendered content of {html_file.name}"
            )

    def test_autoescape_prevents_xss(self, tmp_path: Path) -> None:
        """HTML special characters in comments must be escaped in output."""
        # Create a minimal .TcPOU with <script> in the description
        tcpou_dir = tmp_path / "xss_project"
        tcpou_dir.mkdir()
        (tcpou_dir / "FB_Xss.TcPOU").write_text(
            '<?xml version="1.0"?>'
            '<TcPlcObject><POU Name="FB_Xss">'
            "<Declaration><![CDATA["
            "// :Description: Click <script>alert('xss')</script> here\n"
            "FUNCTION_BLOCK FB_Xss\n"
            "]]></Declaration>"
            "</POU></TcPlcObject>",
            encoding="utf-8",
        )

        out = tmp_path / "xss_out"
        result = HtmlGenerator().generate(str(tcpou_dir), str(out))
        assert result.success

        html = (out / "FB_Xss.html").read_text(encoding="utf-8")
        assert "<script>" not in html
        assert "&lt;script&gt;" in html or "alert" not in html
