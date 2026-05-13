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


@dataclass
class CommentDoc:
    """Parsed documentation comment extracted from a declaration block."""

    description: str = ""
    params: dict[str, str] = field(default_factory=dict)
    returns: str = ""
    remarks: str = ""


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
    plc_name: str  # PLC project (TIPC child) this POU belongs to.
    folder: str = ""  # Path relative to the PLC project root, e.g. "POUs/Functions".


@dataclass
class TaskInfo:
    """A PLC task: cycle time in microseconds, priority, and bound programs."""

    name: str
    cycle_time_us: int | None = None
    priority: int | None = None
    programs: list[str] = field(default_factory=list)


@dataclass
class LibraryRef:
    """A library reference declared in the .plcproj."""

    name: str
    version: str = ""
    placeholder: str | None = None  # e.g. "Tc2_Standard" for placeholder refs.


@dataclass
class PLCSection:
    """One PLC project (.plcproj) within a solution.

    Carries every code symbol declared under that PLC project plus its
    library references. Tasks live at the solution level (.tsproj /
    .TcTTO), not per .plcproj, so they are intentionally absent here.
    """

    name: str
    plcproj_path: str
    pous: list[POURef] = field(default_factory=list)
    gvls: list[str] = field(default_factory=list)
    duts: list[str] = field(default_factory=list)
    libraries: list[LibraryRef] = field(default_factory=list)


@dataclass
class ProjectStructure:
    """A solution's project map, keyed by PLC-project name.

    Multi-project sln returns one entry per .plcproj; a single-project sln
    returns a one-entry dict. Iterate ``plcs.values()`` to walk every PLC
    project. See ADR-0005.
    """

    project_path: str
    plcs: dict[str, PLCSection] = field(default_factory=dict)
    tasks: list[TaskInfo] = field(default_factory=list)


@dataclass
class MethodSignature:
    name: str
    return_type: str
    declaration: str


@dataclass
class PropertySignature:
    name: str
    return_type: str  # e.g. "DWORD", "BOOL"
    declaration: str  # the PROPERTY header declaration
    has_get: bool = True
    has_set: bool = False


@dataclass
class POUInterface:
    pou_name: str
    pou_type: POUType
    declaration: str
    methods: list[MethodSignature] = field(default_factory=list)
    properties: list[PropertySignature] = field(default_factory=list)
    actions: list[str] = field(default_factory=list)


@dataclass
class POUDeclaration:
    """FB-level declaration block only (VAR sections, no methods or bodies).

    Cheaper than ``POUInterface`` when preparing a variable add and you do
    not need method signatures. See ADR-0003.
    """

    pou_name: str
    pou_type: POUType
    declaration: str


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


@dataclass
class DUT:
    """A Data Unit Type — STRUCT, ENUM, UNION, or TYPE alias."""

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


@dataclass
class AssertFailure:
    """A single failed TcUnit assertion (one entry per ``<failure>`` element)."""

    message: str
    expected: str = ""
    actual: str = ""
    line: int = 0


@dataclass
class TestCase:
    name: str
    passed: bool
    asserts: int = 0
    failures: list[AssertFailure] = field(default_factory=list)
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
class TestResultsSummary:
    suites: int = 0
    tests: int = 0
    asserts: int = 0
    failures: int = 0
    errors: int = 0
    duration_seconds: float = 0.0


@dataclass
class TestResults:
    suites: list[TestSuite] = field(default_factory=list)
    summary: TestResultsSummary = field(default_factory=TestResultsSummary)

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
