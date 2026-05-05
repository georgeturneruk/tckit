"""beckhoff_infosys — DocsSearcher adapter targeting infosys.beckhoff.com.

Uses httpx + BeautifulSoup to search and parse Beckhoff documentation.
Pages are cached locally to protect against HTML structure changes and
reduce repeated network calls.

Infosys structure:
  https://infosys.beckhoff.com/content/1033/
    tc3_plc_intro/       - PLC programming guide
    tcplclib_tc2_*/      - TC2 standard libraries
    tcplclib_tc3_*/      - TC3 libraries
    tf6xxx_*/            - TwinCAT functions
    tc3_ads_intro/       - ADS documentation
"""

from tckit.ports.docs_searcher import DocsSearcher
from tckit.ports.types import DocPage, FBDoc, LibraryDoc, SearchResults

INFOSYS_BASE = "https://infosys.beckhoff.com/content/{lang}/"
INFOSYS_SEARCH = "https://infosys.beckhoff.com/index_en.htm#search={query}"


class BeckhoffInfosys(DocsSearcher):
    """Searches and fetches Beckhoff infosys documentation with local page cache."""

    def __init__(self, cache_path: str = "./cache/infosys", lang: str = "1033") -> None:
        self._cache_path = cache_path
        self._lang = lang

    def find_fb(self, fb_name: str) -> FBDoc:
        raise NotImplementedError("beckhoff_infosys.find_fb() not yet implemented")

    def find_library(self, library_name: str) -> LibraryDoc:
        raise NotImplementedError("beckhoff_infosys.find_library() not yet implemented")

    def search(self, query: str, section: str | None = None) -> SearchResults:
        raise NotImplementedError("beckhoff_infosys.search() not yet implemented")

    def get_page(self, url: str) -> DocPage:
        raise NotImplementedError("beckhoff_infosys.get_page() not yet implemented")
