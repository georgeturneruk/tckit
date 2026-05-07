"""_infosys_navigator — navigate Beckhoff infosys without external search.

Uses infosys's own menu.php tree navigation to build a title→URL index for
any documentation section. No DDG, no API keys, no rate limiting.

Algorithm per section:
  1. Fetch <section>/index.html  → extract <meta name="primaryid">
  2. Fetch menu.php?...&id=<primaryid>  → section's top-level page list
  3. Fetch each category page  → collect all child page links recursively
  4. Build {page_title: absolute_url} index and cache it locally

After the first build, all lookups within a section are local cache reads.
"""

from __future__ import annotations

import time
from urllib.parse import urljoin, urlparse, parse_qs

import httpx
from bs4 import BeautifulSoup, Tag

INFOSYS_HOST = "https://infosys.beckhoff.com"
MENU_URL = f"{INFOSYS_HOST}/english/menu/menu.php"

_HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) "
        "Chrome/120.0.0.0 Safari/537.36"
    ),
}

# Sections searched in order of likelihood for find_fb()
KNOWN_SECTIONS = [
    # Standard PLC libraries
    "tcplclib_tc2_standard",
    "tcplclib_tc2_math",
    "tcplclib_tc3_math",
    "tcplclib_tc3_string",
    "tcplclib_tc2_ethercat",
    "tcplclib_tc2_drive",
    "tcplclib_tc2_iofunctions",
    "tcplclib_tc2_system",
    # TwinCAT Functions
    "tf6310_tc3_tcpip",
    "tf6100_tc3_opcua",
    "tf6300_tc3_tcp",
    "tf6xxx_tc3_ads",
    # Documentation
    "tc3_automationinterface",
    "tc3_plc_intro",
    "tc3_ads_intro",
]


# ---------------------------------------------------------------------------
# Low-level fetchers
# ---------------------------------------------------------------------------


def _fetch(url: str, timeout: int = 8) -> BeautifulSoup | None:
    """Fetch a URL and return a BeautifulSoup, or None on error."""
    try:
        resp = httpx.get(url, timeout=timeout, headers=_HEADERS, follow_redirects=True)
        if resp.status_code != 200:
            return None
        return BeautifulSoup(resp.text, "html.parser")
    except (httpx.RequestError, httpx.HTTPStatusError):
        return None


def _meta(soup: BeautifulSoup, name: str) -> str:
    """Extract a <meta name="..."> content value."""
    tag = soup.find("meta", attrs={"name": name})
    if tag and isinstance(tag, Tag):
        return tag.get("content", "")
    return ""


def _clean_title(soup: BeautifulSoup) -> str:
    """Extract page title, stripping Beckhoff branding suffixes."""
    tag = soup.find("title")
    if not tag:
        return ""
    title = tag.get_text(strip=True)
    for suffix in (" - Beckhoff Automation", " | Beckhoff", " - TwinCAT"):
        if title.endswith(suffix):
            title = title[: -len(suffix)]
    return title.strip()


def _strip_id(url: str) -> str:
    """Remove ?id=... query parameter from infosys URLs."""
    if "?" in url:
        return url.split("?")[0]
    return url


def _to_absolute(href: str, base_url: str) -> str:
    """Convert a relative href to an absolute infosys URL."""
    if href.startswith("http"):
        return href
    return urljoin(base_url, href)


# ---------------------------------------------------------------------------
# Section index builder
# ---------------------------------------------------------------------------


def get_section_primaryid(section_path: str) -> str | None:
    """Fetch a section's index.html and return its primaryid meta value."""
    url = f"{INFOSYS_HOST}/content/1033/{section_path}/index.html"
    soup = _fetch(url)
    if soup is None:
        return None
    return _meta(soup, "primaryid") or None


def get_menu_links(section_path: str, primaryid: str) -> list[tuple[str, str]]:
    """Fetch menu.php for a section and return [(url, title)] for that section.

    The primaryid causes menu.php to expand the tree for that section, revealing
    its top-level category pages.
    """
    soup = _fetch(
        MENU_URL,
        timeout=8,
    )
    # Need to pass params - use httpx directly
    try:
        resp = httpx.get(
            MENU_URL,
            params={
                "content": f"../content/1033/{section_path}/index.html",
                "id": primaryid,
            },
            headers=_HEADERS,
            timeout=8,
            follow_redirects=True,
        )
        if resp.status_code != 200:
            return []
        soup = BeautifulSoup(resp.text, "html.parser")
    except (httpx.RequestError, httpx.HTTPStatusError):
        return []

    links: list[tuple[str, str]] = []
    section_base = f"/content/1033/{section_path}/"

    for a in soup.find_all("a", href=True):
        href = str(a.get("href", ""))
        title = a.get_text(strip=True)
        if not title:
            continue
        # Only include links within this section
        clean = _strip_id(href)
        if section_base in clean and not clean.endswith("/index.html"):
            abs_url = _to_absolute(clean, INFOSYS_HOST)
            links.append((abs_url, title))

    return links


def build_section_index(
    section_path: str,
    *,
    polite_delay: float = 0.3,
) -> dict[str, str]:
    """Build a {page_title: url} index for a section by navigating its tree.

    Fetches category pages and collects all child page links recursively.
    Depth-limited to 6 levels to avoid runaway crawls.

    Args:
        section_path: Section path e.g. "tf6310_tc3_tcpip"
        polite_delay: Seconds to wait between HTTP requests (be polite)

    Returns:
        Dict mapping page title (lowercase) → absolute URL.
    """
    primaryid = get_section_primaryid(section_path)
    if not primaryid:
        return {}

    time.sleep(polite_delay)
    top_links = get_menu_links(section_path, primaryid)

    index: dict[str, str] = {}
    section_base = f"{INFOSYS_HOST}/content/1033/{section_path}/"
    index_url = section_base + "index.html"

    visited: set[str] = {index_url}
    queue: list[tuple[str, int]] = [(url, 0) for url, _ in top_links]

    # Seed the index with menu titles (fast, no extra fetch)
    for url, title in top_links:
        index[title.lower()] = url

    while queue:
        url, depth = queue.pop(0)
        if url in visited or depth > 6:
            continue
        visited.add(url)

        time.sleep(polite_delay)
        soup = _fetch(url)
        if soup is None:
            continue

        # Record this page's own title
        title = _clean_title(soup)
        if title:
            index[title.lower()] = url

        # Collect child links within this section
        for a in soup.find_all("a", href=True):
            href = str(a.get("href", ""))
            child_title = a.get_text(strip=True)
            if not href or not child_title:
                continue
            if not href.endswith(".html"):
                continue
            if href.startswith("http") and section_base not in href:
                continue
            abs_url = _to_absolute(href, url)
            if section_base not in abs_url or abs_url == index_url:
                continue
            clean_url = _strip_id(abs_url)
            if clean_url not in visited:
                index[child_title.lower()] = clean_url
                queue.append((clean_url, depth + 1))

    return index


# ---------------------------------------------------------------------------
# Search helpers
# ---------------------------------------------------------------------------


def search_index(
    index: dict[str, str],
    query: str,
    *,
    exact: bool = False,
) -> list[tuple[str, str]]:
    """Search a section index for pages matching a query.

    Args:
        index: {title_lower: url} from build_section_index
        query: Search term (case-insensitive)
        exact: If True, require exact title match; otherwise substring match

    Returns:
        List of (original_query, url) matches, best first.
    """
    q = query.lower()
    results: list[tuple[str, str]] = []

    for title, url in index.items():
        if exact:
            if title == q:
                results.insert(0, (title, url))
        else:
            if title == q:
                results.insert(0, (title, url))  # exact match first
            elif q in title:
                results.append((title, url))

    return results
