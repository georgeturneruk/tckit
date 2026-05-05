"""Integration tests for BeckhoffInfosys — network tests marked separately."""

from pathlib import Path

import pytest

from tckit.adapters.docs_searchers.beckhoff_infosys import BeckhoffInfosys
from tckit.ports.types import DocPage


# ---------------------------------------------------------------------------
# Cache-only tests (no network)
# ---------------------------------------------------------------------------


def test_cache_miss_returns_none(tmp_path: Path) -> None:
    """_load_cache returns None when no cached file exists."""
    infosys = BeckhoffInfosys(cache_path=str(tmp_path / "cache"))
    result = infosys._load_cache("https://infosys.beckhoff.com/content/1033/test.html")
    assert result is None


def test_save_and_load_cache(tmp_path: Path) -> None:
    """Pages saved to cache can be loaded back correctly."""
    infosys = BeckhoffInfosys(cache_path=str(tmp_path / "cache"))
    url = "https://infosys.beckhoff.com/content/1033/sample.html"
    infosys._save_cache(url, "Sample Title", "Sample content text.")

    loaded = infosys._load_cache(url)
    assert loaded is not None
    assert loaded["url"] == url
    assert loaded["title"] == "Sample Title"
    assert loaded["content"] == "Sample content text."
    assert "fetched_at" in loaded


def test_cache_key_is_deterministic(tmp_path: Path) -> None:
    """Same URL always produces the same cache key."""
    infosys = BeckhoffInfosys(cache_path=str(tmp_path / "cache"))
    url = "https://infosys.beckhoff.com/content/1033/test.html"
    assert infosys._cache_key(url) == infosys._cache_key(url)


def test_different_urls_have_different_keys(tmp_path: Path) -> None:
    """Different URLs produce different cache keys."""
    infosys = BeckhoffInfosys(cache_path=str(tmp_path / "cache"))
    key1 = infosys._cache_key("https://infosys.beckhoff.com/a.html")
    key2 = infosys._cache_key("https://infosys.beckhoff.com/b.html")
    assert key1 != key2


def test_get_page_returns_cached(tmp_path: Path) -> None:
    """get_page() returns cached DocPage on second call without network."""
    infosys = BeckhoffInfosys(cache_path=str(tmp_path / "cache"))
    url = "https://infosys.beckhoff.com/content/1033/cached_page.html"
    infosys._save_cache(url, "Cached Page", "This is cached content.")

    page = infosys.get_page(url)
    assert isinstance(page, DocPage)
    assert page.cached is True
    assert page.title == "Cached Page"
    assert page.content == "This is cached content."


def test_fb_candidate_urls_includes_slug(tmp_path: Path) -> None:
    """Candidate URLs for an FB include the lowercased FB name."""
    infosys = BeckhoffInfosys(cache_path=str(tmp_path / "cache"))
    candidates = infosys._fb_candidate_urls("FB_EcCoESdoRead")
    slug = "fb_eccoesdoread"
    assert any(slug in url for url in candidates)


# ---------------------------------------------------------------------------
# Network tests — excluded from CI, run manually with: pytest -m network
# ---------------------------------------------------------------------------


@pytest.mark.network
def test_get_page_fetches_and_caches(tmp_path: Path) -> None:
    """get_page() fetches a real infosys page and writes it to cache."""
    infosys = BeckhoffInfosys(cache_path=str(tmp_path / "cache"))
    url = "https://infosys.beckhoff.com/content/1033/tc3_plc_intro/index.html"
    page = infosys.get_page(url)
    assert isinstance(page, DocPage)
    assert page.cached is False
    assert len(page.content) > 100

    # Second call should be cached
    page2 = infosys.get_page(url)
    assert page2.cached is True
    assert page2.content == page.content


@pytest.mark.network
def test_find_fb_returns_fb_doc(tmp_path: Path) -> None:
    """find_fb() returns a populated FBDoc for a known Beckhoff FB."""
    infosys = BeckhoffInfosys(cache_path=str(tmp_path / "cache"))
    fb_doc = infosys.find_fb("FB_MemSet")
    assert fb_doc.name == "FB_MemSet"
    assert len(fb_doc.description) > 0
    assert fb_doc.url.startswith("https://")
