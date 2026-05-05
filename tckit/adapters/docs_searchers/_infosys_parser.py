"""_infosys_parser — private HTML parsing utilities for infosys.beckhoff.com.

Uses BeautifulSoup to extract clean text, titles, and structured data
(parameter tables) from Beckhoff infosys pages.

Infosys HTML structure (as of 2024):
  - Main content is in <div id="content"> or <div class="topic">
  - Navigation/header/footer can be stripped by tag: <nav>, <header>, <footer>
  - Parameter tables: <table> with <th> headers like "Name", "Type", "Direction"
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from bs4 import BeautifulSoup, Tag

if TYPE_CHECKING:
    pass


def extract_title(soup: BeautifulSoup) -> str:
    """Extract page title from <title> tag, cleaned up."""
    title_tag = soup.find("title")
    if title_tag and title_tag.string:
        # Strip " - Beckhoff Infosys" suffix if present
        title = title_tag.string.strip()
        for suffix in (" - Beckhoff Infosys", " | Beckhoff", " - TwinCAT"):
            if title.endswith(suffix):
                title = title[: -len(suffix)].strip()
        return title
    return ""


def extract_main_content(soup: BeautifulSoup) -> str:
    """Extract the main text content, stripping nav/header/footer boilerplate.

    Returns clean text with whitespace normalised.
    """
    # Remove boilerplate elements
    for tag in soup.find_all(["nav", "header", "footer", "script", "style"]):
        tag.decompose()

    # Try known content containers first
    for selector in ("div#content", "div.topic", "main", "article", "div.content"):
        container = soup.select_one(selector)
        if container and isinstance(container, Tag):
            return _clean_text(container.get_text(separator="\n"))

    # Fall back to body text
    body = soup.find("body")
    if body and isinstance(body, Tag):
        return _clean_text(body.get_text(separator="\n"))

    return _clean_text(soup.get_text(separator="\n"))


def extract_description(soup: BeautifulSoup) -> str:
    """Extract the first meaningful paragraph as the FB/function description."""
    for selector in ("div#content p", "div.topic p", "main p", "p"):
        tag = soup.select_one(selector)
        if tag and isinstance(tag, Tag):
            text = tag.get_text(separator=" ").strip()
            if len(text) > 20:  # ignore trivial fragments
                return text
    return ""


def extract_parameter_table(soup: BeautifulSoup) -> list[dict[str, str]]:
    """Extract parameter rows from Beckhoff parameter tables.

    Returns a list of dicts with keys: name, type, direction, description.
    Handles variations in column ordering and naming.
    """
    rows: list[dict[str, str]] = []

    for table in soup.find_all("table"):
        if not isinstance(table, Tag):
            continue
        header_row = table.find("tr")
        if not header_row or not isinstance(header_row, Tag):
            continue

        headers = [
            th.get_text(strip=True).lower()
            for th in header_row.find_all(["th", "td"])
        ]

        # Only process tables that look like parameter tables
        if not any(h in headers for h in ("name", "variable", "parameter")):
            continue

        col = {
            "name": _find_col(headers, ("name", "variable", "parameter")),
            "type": _find_col(headers, ("type", "data type")),
            "direction": _find_col(headers, ("direction", "access", "i/o")),
            "description": _find_col(headers, ("description", "comment", "meaning")),
        }

        for tr in table.find_all("tr")[1:]:  # skip header row
            if not isinstance(tr, Tag):
                continue
            cells = [td.get_text(strip=True) for td in tr.find_all(["td", "th"])]
            if not cells:
                continue
            rows.append(
                {
                    "name": _cell(cells, col["name"]),
                    "type": _cell(cells, col["type"]),
                    "direction": _cell(cells, col["direction"]),
                    "description": _cell(cells, col["description"]),
                }
            )

    return rows


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


def parse_html(html: str) -> BeautifulSoup:
    return BeautifulSoup(html, "html.parser")


def _clean_text(text: str) -> str:
    """Collapse runs of whitespace/blank lines."""
    lines = [line.strip() for line in text.splitlines()]
    # Remove consecutive blank lines
    result: list[str] = []
    prev_blank = False
    for line in lines:
        is_blank = line == ""
        if is_blank and prev_blank:
            continue
        result.append(line)
        prev_blank = is_blank
    return "\n".join(result).strip()


def _find_col(headers: list[str], candidates: tuple[str, ...]) -> int | None:
    for candidate in candidates:
        for i, h in enumerate(headers):
            if candidate in h:
                return i
    return None


def _cell(cells: list[str], col: int | None) -> str:
    if col is None or col >= len(cells):
        return ""
    return cells[col]
