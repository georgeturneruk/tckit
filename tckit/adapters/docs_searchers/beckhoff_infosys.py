"""beckhoff_infosys — DocsSearcher adapter targeting infosys.beckhoff.com.

Uses infosys's own menu.php tree navigation to build section indexes — no DDG,
no external search APIs, no rate limiting.

How it works:
  1. Each section (e.g. tf6310_tc3_tcpip) has an index.html with a <meta primaryid>
  2. Fetching menu.php?...&id=<primaryid> returns the section's expanded tree
  3. Walking that tree builds a {page_title: url} index for the whole section
  4. The index is cached locally — subsequent lookups are instant local reads

Page content is also cached locally (write-once, keyed by SHA256 of URL).
"""

from __future__ import annotations

import hashlib
import json
from datetime import UTC, datetime
from pathlib import Path

import httpx

from tckit.adapters.docs_searchers._infosys_navigator import (
    KNOWN_SECTIONS,
    build_section_index,
    search_index,
)
from tckit.adapters.docs_searchers._infosys_parser import (
    extract_description,
    extract_main_content,
    extract_parameter_table,
    extract_title,
    parse_html,
)
from tckit.ports.docs_searcher import DocsSearcher
from tckit.ports.types import (
    DocPage,
    FBDoc,
    LibraryDoc,
    ParameterDoc,
    SearchResult,
    SearchResults,
)

INFOSYS_HOST = "https://infosys.beckhoff.com"

_HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/120.0.0.0 Safari/537.36"
    ),
}


class BeckhoffInfosys(DocsSearcher):
    """Searches and fetches Beckhoff infosys documentation.

    Uses infosys's own navigation structure to build section indexes — no
    external search required. Section indexes are cached locally.
    """

    def __init__(self, cache_path: str = "./cache/infosys", lang: str = "1033") -> None:
        self._cache_dir = Path(cache_path)
        self._lang = lang

    # ------------------------------------------------------------------
    # DocsSearcher interface
    # ------------------------------------------------------------------

    def get_page(self, url: str) -> DocPage:
        """Fetch a page from infosys (or return from cache).

        Accepts direct content URLs or english.php wrapper URLs — both handled.
        """
        url = self._normalise_url(url)

        cached = self._load_page_cache(url)
        if cached is not None:
            return DocPage(
                url=cached["url"],
                title=cached["title"],
                content=cached["content"],
                cached=True,
            )

        resp = httpx.get(url, follow_redirects=True, timeout=10, headers=_HEADERS)
        resp.raise_for_status()

        soup = parse_html(resp.text)
        title = extract_title(soup)
        content = extract_main_content(soup)

        self._save_page_cache(url, title, content)
        return DocPage(url=url, title=title, content=content, cached=False)

    def search(self, query: str, section: str | None = None) -> SearchResults:
        """Search infosys by scanning section indexes.

        Args:
            query: Free-text search term.
            section: Optional section path to restrict search (e.g. "tf6310_tc3_tcpip").
        """
        sections = [section] if section else KNOWN_SECTIONS
        results: list[SearchResult] = []

        for sec in sections:
            index = self._load_section_index(sec)
            if not index:
                continue
            matches = search_index(index, query)
            for title, url in matches[:3]:
                try:
                    page = self.get_page(url)
                    if page.title and "Information System" not in page.title:
                        snippet = page.content[:200].replace("\n", " ")
                        results.append(SearchResult(title=page.title, url=url, snippet=snippet))
                except (httpx.HTTPStatusError, httpx.RequestError):
                    continue
            if len(results) >= 5:
                break

        return SearchResults(query=query, results=results)

    def find_fb(self, fb_name: str) -> FBDoc:
        """Locate and parse the infosys page for a Function Block.

        Searches known sections by building/loading their page indexes, then
        fetches and parses the matching page.

        Raises:
            FileNotFoundError: If no infosys page can be found for fb_name.
        """
        for section in KNOWN_SECTIONS:
            url = self._find_in_section(section, fb_name)
            if url is None:
                continue

            try:
                resp = httpx.get(url, follow_redirects=True, timeout=10, headers=_HEADERS)
                if resp.status_code != 200:
                    continue
            except httpx.RequestError:
                continue

            soup = parse_html(resp.text)
            title = extract_title(soup)

            if not title or "Information System" in title:
                continue

            description = extract_description(soup)
            content = extract_main_content(soup)
            self._save_page_cache(url, title, content)

            param_rows = extract_parameter_table(soup)
            inputs = [
                ParameterDoc(
                    name=r["name"],
                    type=r["type"],
                    direction=r["direction"],
                    description=r["description"],
                )
                for r in param_rows
                if r["direction"].lower() in ("input", "in", "var_input", "")
            ]
            outputs = [
                ParameterDoc(
                    name=r["name"],
                    type=r["type"],
                    direction=r["direction"],
                    description=r["description"],
                )
                for r in param_rows
                if r["direction"].lower() in ("output", "out", "var_output")
            ]

            return FBDoc(
                name=fb_name,
                description=description,
                url=url,
                inputs=inputs,
                outputs=outputs,
            )

        raise FileNotFoundError(
            f"Could not find infosys page for {fb_name!r}. "
            f"Searched {len(KNOWN_SECTIONS)} section(s). "
            f"Try get_page() with a known URL, or add the section to KNOWN_SECTIONS."
        )

    def find_library(self, library_name: str) -> LibraryDoc:
        """Locate the infosys overview page for a library.

        Raises:
            FileNotFoundError: If no infosys page can be found.
        """
        for section in KNOWN_SECTIONS:
            url = self._find_in_section(section, library_name)
            if url is None:
                continue
            try:
                page = self.get_page(url)
                if page.title and "Information System" not in page.title:
                    return LibraryDoc(
                        name=library_name,
                        description=page.content[:300],
                        url=url,
                    )
            except (httpx.HTTPStatusError, httpx.RequestError):
                continue

        raise FileNotFoundError(f"Could not find infosys page for library {library_name!r}.")

    # ------------------------------------------------------------------
    # Section index management
    # ------------------------------------------------------------------

    def _find_in_section(self, section: str, name: str) -> str | None:
        """Return a URL for the named page within a section, or None.

        Tries the full name first, then strips common TwinCAT FB prefixes
        (FB_, FC_, FUN_, STLB_) to handle IEC standard library pages like
        "TON" which are indexed without the FB_ prefix.
        """
        # Try cache first
        index = self._load_section_index(section)

        if index:
            url = self._search_with_aliases(index, name)
            if url:
                return url
            # Cache exists but no match — don't rebuild
            return None

        # Build index for this section (first time only)
        index = build_section_index(section)
        if index:
            self._save_section_index(section, index)
            return self._search_with_aliases(index, name)

        return None

    @staticmethod
    def _search_with_aliases(index: dict[str, str], name: str) -> str | None:
        """Search with the given name and common prefix-stripped variants."""
        candidates = [name]
        # Strip common TwinCAT prefixes so "FB_TON" also matches "TON"
        for prefix in ("FB_", "FC_", "FUN_", "STLB_", "ST_", "E_", "F_"):
            if name.upper().startswith(prefix):
                candidates.append(name[len(prefix):])
                break

        for candidate in candidates:
            matches = search_index(index, candidate)
            if matches:
                return matches[0][1]
        return None

    def _section_cache_path(self, section: str) -> Path:
        key = hashlib.sha256(section.encode()).hexdigest()[:16]
        return self._cache_dir / f"section_{key}.json"

    def _load_section_index(self, section: str) -> dict[str, str] | None:
        path = self._section_cache_path(section)
        if path.exists():
            try:
                data = json.loads(path.read_text(encoding="utf-8"))
                return data.get("pages", {})
            except (json.JSONDecodeError, OSError):
                pass
        return None

    def _save_section_index(self, section: str, index: dict[str, str]) -> None:
        self._cache_dir.mkdir(parents=True, exist_ok=True)
        path = self._section_cache_path(section)
        path.write_text(
            json.dumps(
                {
                    "section": section,
                    "built_at": datetime.now(tz=UTC).isoformat(),
                    "pages": index,
                },
                indent=2,
                ensure_ascii=False,
            ),
            encoding="utf-8",
        )

    # ------------------------------------------------------------------
    # Page content cache
    # ------------------------------------------------------------------

    def _cache_key(self, url: str) -> str:
        return hashlib.sha256(url.encode()).hexdigest()[:16]

    def _page_cache_path(self, url: str) -> Path:
        return self._cache_dir / f"{self._cache_key(url)}.json"

    def _load_page_cache(self, url: str) -> dict | None:
        path = self._page_cache_path(url)
        if path.exists():
            try:
                return json.loads(path.read_text(encoding="utf-8"))
            except (json.JSONDecodeError, OSError):
                pass
        return None

    def _save_page_cache(self, url: str, title: str, content: str) -> None:
        self._cache_dir.mkdir(parents=True, exist_ok=True)
        entry = {
            "url": url,
            "title": title,
            "content": content,
            "fetched_at": datetime.now(tz=UTC).isoformat(),
        }
        self._page_cache_path(url).write_text(
            json.dumps(entry, ensure_ascii=False, indent=2), encoding="utf-8"
        )

    # ------------------------------------------------------------------
    # URL helpers
    # ------------------------------------------------------------------

    def _normalise_url(self, url: str) -> str:
        """Convert english.php wrapper URLs to direct content URLs."""
        if "english.php" in url and "content=" in url:
            import urllib.parse
            qs = urllib.parse.parse_qs(urllib.parse.urlparse(url).query)
            content_path = qs.get("content", [""])[0].lstrip("./").lstrip("../")
            return f"{INFOSYS_HOST}/{content_path}"
        return url
