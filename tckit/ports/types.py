"""Shared dataclasses returned by all port methods."""

from dataclasses import dataclass, field
from enum import StrEnum
from typing import Any

# ---------------------------------------------------------------------------
# Common
# ---------------------------------------------------------------------------


@dataclass
class Result:
    success: bool
    error: str | None = None
    details: dict[str, Any] = field(default_factory=dict)


# ---------------------------------------------------------------------------
# ProjectReader types
# ---------------------------------------------------------------------------


class POUType(StrEnum):
    FUNCTION_BLOCK = "function_block"
    FUNCTION = "function"
    PROGRAM = "program"
    INTERFACE = "interface"


@dataclass
class POURef:
    name: str
    pou_type: POUType
    path: str


@dataclass
class ProjectStructure:
    project_path: str
    pous: list[POURef] = field(default_factory=list)
    gvls: list[str] = field(default_factory=list)
    tasks: list[str] = field(default_factory=list)


@dataclass
class MethodSignature:
    name: str
    return_type: str
    declaration: str


@dataclass
class POUInterface:
    pou_name: str
    pou_type: POUType
    declaration: str
    methods: list[MethodSignature] = field(default_factory=list)
    properties: list[str] = field(default_factory=list)
    actions: list[str] = field(default_factory=list)


@dataclass
class POUItem:
    pou_name: str
    item_name: str
    declaration: str
    body: str


@dataclass
class GVL:
    name: str
    path: str
    declaration: str


# ---------------------------------------------------------------------------
# BuildRunner types
# ---------------------------------------------------------------------------


class BuildStatus(StrEnum):
    IDLE = "idle"
    BUILDING = "building"
    SUCCESS = "success"
    ERROR = "error"


@dataclass
class BuildError:
    file: str
    line: int
    message: str
    severity: str = "error"


@dataclass
class BuildResult:
    success: bool
    errors: list[BuildError] = field(default_factory=list)
    warnings: list[BuildError] = field(default_factory=list)
    duration_seconds: float | None = None


# ---------------------------------------------------------------------------
# TestRunner types
# ---------------------------------------------------------------------------


class TestStatus(StrEnum):
    IDLE = "idle"
    RUNNING = "running"
    COMPLETE = "complete"
    TIMEOUT = "timeout"
    ERROR = "error"


@dataclass
class TestCase:
    name: str
    passed: bool
    message: str | None = None
    duration_seconds: float | None = None


@dataclass
class TestSuite:
    name: str
    tests: list[TestCase] = field(default_factory=list)

    @property
    def passed(self) -> int:
        return sum(1 for t in self.tests if t.passed)

    @property
    def failed(self) -> int:
        return sum(1 for t in self.tests if not t.passed)


@dataclass
class TestResults:
    suites: list[TestSuite] = field(default_factory=list)

    @property
    def total_passed(self) -> int:
        return sum(s.passed for s in self.suites)

    @property
    def total_failed(self) -> int:
        return sum(s.failed for s in self.suites)

    @property
    def success(self) -> bool:
        return self.total_failed == 0


# ---------------------------------------------------------------------------
# DocGenerator types
# ---------------------------------------------------------------------------


class DocStatus(StrEnum):
    IDLE = "idle"
    GENERATING = "generating"
    COMPLETE = "complete"
    ERROR = "error"


# ---------------------------------------------------------------------------
# DocsSearcher types
# ---------------------------------------------------------------------------


@dataclass
class ParameterDoc:
    name: str
    type: str
    direction: str
    description: str


@dataclass
class FBDoc:
    name: str
    description: str
    url: str
    inputs: list[ParameterDoc] = field(default_factory=list)
    outputs: list[ParameterDoc] = field(default_factory=list)
    notes: str | None = None


@dataclass
class LibraryDoc:
    name: str
    description: str
    url: str
    function_blocks: list[str] = field(default_factory=list)


@dataclass
class SearchResult:
    title: str
    url: str
    snippet: str


@dataclass
class SearchResults:
    query: str
    results: list[SearchResult] = field(default_factory=list)


@dataclass
class DocPage:
    url: str
    title: str
    content: str
    cached: bool = False
