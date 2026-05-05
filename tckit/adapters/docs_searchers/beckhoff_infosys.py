"""beckhoff_infosys — DocsSearcher adapter targeting infosys.beckhoff.com.

Uses httpx + BeautifulSoup to search and parse Beckhoff documentation.
Pages are cached locally as JSON files keyed by sha256(url)[:16] to protect
against HTML structure changes and reduce repeated network calls.

Cache schema: {"url": str, "title": str, "content": str, "fetched_at": str}
Cache is write-once, never auto-invalidated.

Infosys URL structure:
  https://infosys.beckhoff.com/content/1033/
    tc3_plc_intro/       - PLC programming guide
    tcplclib_tc2_*/      - TC2 standard libraries
    tcplclib_tc3_*/      - TC3 libraries
    tf6xxx_*/            - TwinCAT functions
    tc3_ads_intro/       - ADS documentation
"""

from __future__ import annotations

import hashlib
import json
from datetime import UTC, datetime
from pathlib import Path

import httpx

from tckit.adapters.docs_searchers._infosys_parser import (
    extract_description,
    extract_main_content,
    extract_parameter_table,
    extract_title,
    parse_html,
)
from tckit.ports.docs_searcher import DocsSearcher
from tckit.ports.types import DocPage, FBDoc, LibraryDoc, ParameterDoc, SearchResult, SearchResults

INFOSYS_BASE = "https://infosys.beckhoff.com/content/{lang}/"

# Known section paths — used to construct candidate URLs for search and find_fb.
# These are stable infosys URL prefixes ordered by relevance.
_SECTION_ROOTS = [
    "tcplclib_tc2_standard",
    "tcplclib_tc3_math",
    "tcplclib_tc3_string",
    "tf6xxx_tc3_ethercat",
    "tf6xxx_tc3_ads",
    "tc3_plc_intro",
    "tc3_ads_intro",
]


class BeckhoffInfosys(DocsSearcher):
    """Searches and fetches Beckhoff infosys documentation with local page cache."""

    def __init__(self, cache_path: str = "./cache/infosys", lang: str = "1033") -> None:
        self._cache_dir = Path(cache_path)
        self._lang = lang
        self._base = INFOSYS_BASE.format(lang=lang)

    # ------------------------------------------------------------------
    # DocsSearcher interface
    # ------------------------------------------------------------------

    def get_page(self, url: str) -> DocPage:
        """Fetch a page from infosys (or return from cache).

        Raises:
            httpx.HTTPStatusError: If the server returns a non-2xx response.
            httpx.RequestError: If the network request fails.
        """
        cached = self._load_cache(url)
        if cached is not None:
            return DocPage(
                url=cached["url"],
                title=cached["title"],
                content=cached["content"],
                cached=True,
            )

        response = httpx.get(url, follow_redirects=True, timeout=10)
        response.raise_for_status()

        soup = parse_html(response.text)
        title = extract_title(soup)
        content = extract_main_content(soup)

        self._save_cache(url, title, content)
        return DocPage(url=url, title=title, content=content, cached=False)

    def search(self, query: str, section: str | None = None) -> SearchResults:
        """Search infosys by constructing candidate URLs from known section patterns.

        Infosys search is JavaScript-rendered so direct search URLs are not
        parseable. Instead, we probe known section index pages that may match.

        Returns:
            SearchResults with any successfully fetched pages as results.
        """
        results: list[SearchResult] = []
        query_lower = query.lower().replace(" ", "_")

        sections = [section] if section else _SECTION_ROOTS
        for sec in sections:
            candidate = f"{self._base}{sec}/{query_lower}.html"
            try:
                page = self.get_page(candidate)
                if page.title:
                    snippet = page.content[:200].replace("\n", " ")
                    results.append(
                        SearchResult(title=page.title, url=candidate, snippet=snippet)
                    )
            except (httpx.HTTPStatusError, httpx.RequestError):
                continue

        return SearchResults(query=query, results=results)

    def find_fb(self, fb_name: str) -> FBDoc:
        """Locate and parse the infosys page for a Function Block.

        Constructs candidate URLs from the FB name using known infosys
        URL patterns and tries each until one succeeds.

        Raises:
            FileNotFoundError: If no infosys page can be found for fb_name.
        """
        candidates = self._fb_candidate_urls(fb_name)
        for url in candidates:
            try:
                response = httpx.get(url, follow_redirects=True, timeout=10)
                if response.status_code != 200:
                    continue
            except httpx.RequestError:
                continue

            soup = parse_html(response.text)
            title = extract_title(soup)
            description = extract_description(soup)
            content = extract_main_content(soup)
            self._save_cache(url, title, content)

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
            f"Tried {len(candidates)} candidate URL(s)."
        )

    def find_library(self, library_name: str) -> LibraryDoc:
        """Locate and parse the infosys overview page for a library.

        Raises:
            FileNotFoundError: If no infosys page can be found for library_name.
        """
        candidates = self._library_candidate_urls(library_name)
        for url in candidates:
            try:
                page = self.get_page(url)
            except (httpx.HTTPStatusError, httpx.RequestError):
                continue

            return LibraryDoc(
                name=library_name,
                description=page.content[:300],
                url=url,
            )

        raise FileNotFoundError(
            f"Could not find infosys page for library {library_name!r}."
        )

    # ------------------------------------------------------------------
    # Cache helpers
    # ------------------------------------------------------------------

    def _cache_key(self, url: str) -> str:
        return hashlib.sha256(url.encode()).hexdigest()[:16]

    def _cache_path(self, url: str) -> Path:
        return self._cache_dir / f"{self._cache_key(url)}.json"

    def _load_cache(self, url: str) -> dict | None:
        path = self._cache_path(url)
        if path.exists():
            try:
                return json.loads(path.read_text(encoding="utf-8"))
            except (json.JSONDecodeError, OSError):
                return None
        return None

    def _save_cache(self, url: str, title: str, content: str) -> None:
        self._cache_dir.mkdir(parents=True, exist_ok=True)
        entry = {
            "url": url,
            "title": title,
            "content": content,
            "fetched_at": datetime.now(tz=UTC).isoformat(),
        }
        path = self._cache_path(url)
        path.write_text(json.dumps(entry, ensure_ascii=False, indent=2), encoding="utf-8")

    # ------------------------------------------------------------------
    # URL construction helpers
    # ------------------------------------------------------------------

    def _fb_candidate_urls(self, fb_name: str) -> list[str]:
        """Generate candidate infosys URLs for a Function Block name.

        Beckhoff infosys URLs are lowercase with underscores. FB names like
        FB_EcCoESdoRead map to paths like fb_eccoesdoread.html or
        fb_eccoesdoread/fb_eccoesdoread.html.
        """
        slug = fb_name.lower()
        candidates = []
        for section in _SECTION_ROOTS:
            base = f"{self._base}{section}/"
            candidates.append(f"{base}{slug}.html")
            candidates.append(f"{base}{slug}/{slug}.html")
        return candidates

    def _library_candidate_urls(self, library_name: str) -> list[str]:
        slug = library_name.lower().replace(" ", "_").replace("-", "_")
        candidates = []
        for section in _SECTION_ROOTS:
            if slug in section:
                candidates.append(f"{self._base}{section}/index.html")
                candidates.append(f"{self._base}{section}/introduction.html")
        # Generic fallback
        candidates.append(f"{self._base}{slug}/index.html")
        return candidates
