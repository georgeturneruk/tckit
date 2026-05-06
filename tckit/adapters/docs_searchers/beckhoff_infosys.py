"""beckhoff_infosys — DocsSearcher adapter targeting infosys.beckhoff.com.

Uses DuckDuckGo HTML search to resolve Beckhoff infosys page URLs (which use
opaque numeric IDs), then fetches and parses the content directly.

Pages are cached locally as JSON files keyed by sha256(url)[:16] to protect
against HTML structure changes and reduce repeated network calls.

Cache schema: {"url": str, "title": str, "content": str, "fetched_at": str}
Cache is write-once, never auto-invalidated.

URL resolution strategy:
  DuckDuckGo HTML search returns infosys.beckhoff.com/content/1033/... URLs
  directly in the .result__url element — no API key needed, no JS rendering.
  These direct content URLs serve parseable HTML unlike the english.php frameset.
"""

from __future__ import annotations

import hashlib
import json
import time
from datetime import UTC, datetime
from pathlib import Path

import httpx
from bs4 import BeautifulSoup

from tckit.adapters.docs_searchers._infosys_parser import (
    extract_description,
    extract_main_content,
    extract_parameter_table,
    extract_title,
    parse_html,
)
from tckit.ports.docs_searcher import DocsSearcher
from tckit.ports.types import DocPage, FBDoc, LibraryDoc, ParameterDoc, SearchResult, SearchResults

INFOSYS_HOST = "https://infosys.beckhoff.com"
DDG_HTML_URL = "https://html.duckduckgo.com/html/"

_DDG_HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/120.0.0.0 Safari/537.36"
    ),
    "Accept-Language": "en-US,en;q=0.9",
}


class BeckhoffInfosys(DocsSearcher):
    """Searches and fetches Beckhoff infosys documentation with local page cache.

    Uses DuckDuckGo HTML search to resolve opaque infosys numeric page IDs,
    then fetches the direct content URL. No API key required.
    """

    def __init__(self, cache_path: str = "./cache/infosys", lang: str = "1033") -> None:
        self._cache_dir = Path(cache_path)
        self._lang = lang

    # ------------------------------------------------------------------
    # DocsSearcher interface
    # ------------------------------------------------------------------

    def get_page(self, url: str) -> DocPage:
        """Fetch a page from infosys (or return from cache).

        Accepts either:
          - Direct content URLs: https://infosys.beckhoff.com/content/1033/...
          - english.php wrapper: converted to direct URL automatically

        Raises:
            httpx.HTTPStatusError: If the server returns a non-2xx response.
            httpx.RequestError: If the network request fails.
        """
        url = self._normalise_url(url)

        cached = self._load_cache(url)
        if cached is not None:
            return DocPage(
                url=cached["url"],
                title=cached["title"],
                content=cached["content"],
                cached=True,
            )

        response = httpx.get(url, follow_redirects=True, timeout=10, headers=_DDG_HEADERS)
        response.raise_for_status()

        soup = parse_html(response.text)
        title = extract_title(soup)
        content = extract_main_content(soup)

        self._save_cache(url, title, content)
        return DocPage(url=url, title=title, content=content, cached=False)

    def search(self, query: str, section: str | None = None) -> SearchResults:
        """Search infosys via DuckDuckGo HTML search.

        Constructs a `site:infosys.beckhoff.com` query and parses the first
        page of DDG HTML results to extract direct content URLs.

        Args:
            query: Free-text search query.
            section: Optional infosys section path to scope results
                     (e.g. "tf6310_tc3_tcpip").

        Returns:
            SearchResults with up to 5 matching pages.
        """
        ddg_query = "site:infosys.beckhoff.com"
        if section:
            ddg_query += f" {section}"
        ddg_query += f" {query}"

        urls = self._ddg_search(ddg_query, max_results=5)
        results: list[SearchResult] = []

        for url in urls:
            try:
                page = self.get_page(url)
                if page.title and "Beckhoff Information System" not in page.title:
                    snippet = page.content[:200].replace("\n", " ")
                    results.append(SearchResult(title=page.title, url=url, snippet=snippet))
            except (httpx.HTTPStatusError, httpx.RequestError):
                continue

        return SearchResults(query=query, results=results)

    def find_fb(self, fb_name: str) -> FBDoc:
        """Locate and parse the infosys page for a Function Block.

        Uses DuckDuckGo to find the correct numeric URL, then fetches and
        parses the page for description and parameter tables.

        Raises:
            FileNotFoundError: If no infosys page can be found for fb_name.
        """
        ddg_query = f"site:infosys.beckhoff.com {fb_name}"
        urls = self._ddg_search(ddg_query, max_results=8)

        for url in urls:
            try:
                response = httpx.get(url, follow_redirects=True, timeout=10, headers=_DDG_HEADERS)
                if response.status_code != 200:
                    continue
            except httpx.RequestError:
                continue

            soup = parse_html(response.text)
            title = extract_title(soup)

            # Skip frameset / index pages that aren't actual FB docs
            if not title or "Information System" in title:
                continue

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
            f"Tried {len(urls)} DDG result(s)."
        )

    def find_library(self, library_name: str) -> LibraryDoc:
        """Locate and parse the infosys overview page for a library.

        Raises:
            FileNotFoundError: If no infosys page can be found for library_name.
        """
        ddg_query = f"site:infosys.beckhoff.com {library_name} overview"
        urls = self._ddg_search(ddg_query, max_results=5)

        for url in urls:
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

        raise FileNotFoundError(
            f"Could not find infosys page for library {library_name!r}."
        )

    # ------------------------------------------------------------------
    # DuckDuckGo search
    # ------------------------------------------------------------------

    def _ddg_search(self, query: str, max_results: int = 5) -> list[str]:
        """Search DuckDuckGo HTML endpoint and return direct infosys content URLs.

        Results are cached so the same query never hits DDG twice.
        Retries once with a 3-second delay on rate-limit (HTTP 202/429) responses.
        """
        # Check DDG result cache first
        cached = self._load_ddg_cache(query)
        if cached is not None:
            return cached[:max_results]

        urls = self._ddg_fetch(query)

        # On rate-limit (202/empty), wait and retry once
        if not urls:
            time.sleep(3)
            urls = self._ddg_fetch(query)

        if urls:
            self._save_ddg_cache(query, urls)

        return urls[:max_results]

    def _ddg_fetch(self, query: str) -> list[str]:
        """Single DDG fetch — parses .result__url elements for infosys content URLs."""
        try:
            response = httpx.get(
                DDG_HTML_URL,
                params={"q": query},
                headers=_DDG_HEADERS,
                timeout=10,
                follow_redirects=True,
            )
            if response.status_code not in (200, 202):
                response.raise_for_status()
        except (httpx.HTTPStatusError, httpx.RequestError):
            return []

        soup = BeautifulSoup(response.text, "html.parser")
        urls: list[str] = []

        for result in soup.select(".result"):
            url_el = result.select_one(".result__url")
            if url_el is None:
                continue
            raw = url_el.get_text(strip=True)
            if not raw.startswith("http"):
                raw = "https://" + raw
            if "infosys.beckhoff.com" in raw and "/content/" in raw:
                urls.append(raw)

        return urls

    def _load_ddg_cache(self, query: str) -> list[str] | None:
        key = "ddg_" + hashlib.sha256(query.encode()).hexdigest()[:16]
        path = self._cache_dir / f"{key}.json"
        if path.exists():
            try:
                data = json.loads(path.read_text(encoding="utf-8"))
                return data.get("urls", [])
            except (json.JSONDecodeError, OSError):
                pass
        return None

    def _save_ddg_cache(self, query: str, urls: list[str]) -> None:
        self._cache_dir.mkdir(parents=True, exist_ok=True)
        key = "ddg_" + hashlib.sha256(query.encode()).hexdigest()[:16]
        path = self._cache_dir / f"{key}.json"
        entry = {"query": query, "urls": urls, "fetched_at": datetime.now(tz=UTC).isoformat()}
        path.write_text(
            json.dumps(entry, indent=2),
            encoding="utf-8",
        )

    # ------------------------------------------------------------------
    # URL helpers
    # ------------------------------------------------------------------

    def _normalise_url(self, url: str) -> str:
        """Convert english.php wrapper URLs to direct content URLs.

        english.php?content=../content/1033/section/page.html
          → https://infosys.beckhoff.com/content/1033/section/page.html
        """
        if "english.php" in url and "content=" in url:
            # Extract the content= parameter value
            import urllib.parse
            parsed = urllib.parse.urlparse(url)
            qs = urllib.parse.parse_qs(parsed.query)
            content_path = qs.get("content", [""])[0]
            # Strip leading ../
            content_path = content_path.lstrip("./").lstrip("../")
            return f"{INFOSYS_HOST}/{content_path}"
        return url

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
