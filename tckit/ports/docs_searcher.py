"""DocsSearcher port — targeted search and fetch of Beckhoff infosys documentation."""

from abc import ABC, abstractmethod

from tckit.ports.types import DocPage, FBDoc, LibraryDoc, SearchResults


class DocsSearcher(ABC):
    """Search and retrieve Beckhoff infosys documentation.

    Always call find_fb() before writing code that uses an unfamiliar Beckhoff FB.
    Beckhoff FBs have specific input/output conventions and timing requirements
    that are not reliably in training data, especially for newer TF libraries.
    """

    @abstractmethod
    def find_fb(self, fb_name: str) -> FBDoc:
        """Search for and fetch documentation for a specific Function Block.

        This is the most common call — it combines search and page fetch in one.

        :param fb_name: Name of the FB (e.g. ``FB_EcCoESdoRead``).
        :returns: FBDoc with inputs, outputs, and timing notes.
        """
        ...

    @abstractmethod
    def find_library(self, library_name: str) -> LibraryDoc:
        """Fetch the top-level documentation page for a Beckhoff library.

        :param library_name: Library name (e.g. ``Tc2_EtherCAT``).
        :returns: LibraryDoc with description and list of FBs.
        """
        ...

    @abstractmethod
    def search(self, query: str, section: str | None = None) -> SearchResults:
        """Search infosys for a term, optionally scoped to a section.

        :param query: Search term.
        :param section: Optional infosys section prefix (e.g. ``tcplclib_tc2_ethercat``).
        :returns: SearchResults with titles, URLs, and snippets.
        """
        ...

    @abstractmethod
    def get_page(self, url: str) -> DocPage:
        """Fetch and parse a specific infosys page.

        Pages are cached locally to protect against HTML structure changes.

        :param url: Full infosys URL.
        :returns: DocPage with structured content.
        """
        ...
