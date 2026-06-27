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


class DUTKind(StrEnum):
    """Kind discriminator for Data Unit Type creation.

    Backs ``ProjectWriter.add_dut`` (which accepts only STRUCT/ENUM/UNION
    on write) and ``DUT.dut_kind`` on read. ALIAS DUTs map to TwinCAT
    SubType 623 and are read-only for now: writer-side support is held
    back until a real use case appears.
    """

    STRUCT = "struct"
    ENUM = "enum"
    UNION = "union"
    ALIAS = "alias"


@dataclass
class POURef:
    name: str
    pou_type: POUType
    path: str
    plc_name: str  # PLC project (TIPC child) this POU belongs to.
    folder: str = ""  # Path relative to the PLC project root, e.g. "POUs/Functions".


@dataclass
class GVLRef:
    """A GVL entry in a project structure listing.

    Mirror of :class:`POURef`. Distinct from :class:`GVL`, which carries
    the parsed declaration text returned by ``get_gvl``.
    """

    name: str
    path: str
    plc_name: str
    folder: str = ""


@dataclass
class DUTRef:
    """A DUT entry in a project structure listing.

    Mirror of :class:`POURef`. ``dut_kind`` discriminates STRUCT / ENUM /
    UNION / ALIAS so callers can prefilter without re-parsing each file.
    """

    name: str
    path: str
    plc_name: str
    dut_kind: "DUTKind"
    folder: str = ""


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
    gvls: list[GVLRef] = field(default_factory=list)
    duts: list[DUTRef] = field(default_factory=list)
    libraries: list[LibraryRef] = field(default_factory=list)


@dataclass
class ProjectStructure:
    """A solution's project map, keyed by PLC-project name.

    Multi-project sln returns one entry per .plcproj; a single-project sln
    returns a one-entry dict. Iterate ``plcs.values()`` to walk every PLC
    project. See ADR-0005.

    ``solution_path`` is the absolute path to the .sln file the reader
    resolved during the walk — pass it as ``project_path`` on the follow-up
    ``build()`` call. Empty when the project has no .sln (e.g. a bare
    .tsproj layout).
    """

    project_path: str
    solution_path: str = ""
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
    """A Data Unit Type — STRUCT, ENUM, UNION, or TYPE alias.

    ``dut_kind`` discriminates the four variants; ``base_type`` is the
    aliased type for ALIAS DUTs (e.g. ``"LREAL"``,
    ``"ARRAY [0..9] OF INT"``) and empty for the other kinds.
    """

    name: str
    path: str
    declaration: str
    dut_kind: DUTKind = DUTKind.STRUCT
    base_type: str = ""


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
    # PLC Error List detail (populated when the build reads the IDE Error
    # List). ``code`` is the compiler code (e.g. "C0046"); ``project`` is
    # the PLC project the item belongs to. Both default empty so older
    # callers and the build-output fallback stay compatible.
    code: str = ""
    project: str = ""


@dataclass
class BuildResult:
    success: bool
    errors: list[BuildError] = field(default_factory=list)
    warnings: list[BuildError] = field(default_factory=list)
    # Info-level Error List messages, when the IDE surfaces them.
    infos: list[BuildError] = field(default_factory=list)
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
    # Populated when the bridge's XML-path resolver picked between
    # multiple UmRT candidates; empty otherwise. Surfaces ambiguity so
    # operators can pin via TCKIT_TCUNIT_XML_PATH. See ADR-0011.
    warning: str = ""

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


# ---------------------------------------------------------------------------
# HardwareInspector types
# ---------------------------------------------------------------------------


@dataclass
class EtherCatMasterInfo:
    """Basic identity of an EtherCAT master on the target system."""

    net_id: str
    name: str
    port: int = 65535  # 0xFFFF — the standard EtherCAT master AMS port


@dataclass
class EtherCatSlaveInfo:
    """Per-slave identity, state, and CRC error counts."""

    address: int
    name: str
    vendor_id: int
    product_code: int
    revision: int
    serial: int
    state: str  # "INIT" | "PREOP" | "BOOTSTRAP" | "SAFEOP" | "OP" | "ERROR" | "UNKNOWN"
    link_ok: bool
    crc_errors_a: int
    crc_errors_b: int
    crc_errors_c: int
    crc_errors_d: int


@dataclass
class EtherCatMasterState:
    """Master-level diagnostic flags decoded from IG 0x45."""

    state_flags: int
    link_error: bool
    io_locked: bool
    watchdog_triggered: bool
    dc_out_of_sync: bool


@dataclass
class EtherCatStatus:
    """Full EtherCAT status snapshot: master state + slave table."""

    master: EtherCatMasterState
    slaves: list[EtherCatSlaveInfo]


# ---------------------------------------------------------------------------
# HardwareInspector types — IPC hardware
# ---------------------------------------------------------------------------


@dataclass
class IpcCpuInfo:
    """CPU diagnostic data from MDP module type 0x000B."""

    temperature_c: int | None
    usage_pct: int
    frequency_mhz: int


@dataclass
class IpcMemoryInfo:
    """System memory from MDP module type 0x000C (values in MB)."""

    total_mb: int
    free_mb: int

    @property
    def used_mb(self) -> int:
        return self.total_mb - self.free_mb


@dataclass
class IpcFanInfo:
    """Fan speed from MDP module type 0x001B."""

    index: int
    rpm: int


@dataclass
class IpcNicInfo:
    """Network adapter info from MDP module type 0x0002."""

    index: int
    mac: str
    ipv4: str


@dataclass
class IpcUpsInfo:
    """UPS status from MDP module type 0x001E."""

    battery_pct: int
    power_ok: bool
    battery_ok: bool
    power_fail_count: int


@dataclass
class IpcHardware:
    """Full IPC hardware snapshot — all discovered MDP modules."""

    twincat_version: str | None
    cpu: IpcCpuInfo | None
    memory: IpcMemoryInfo | None
    fans: list[IpcFanInfo]
    nics: list[IpcNicInfo]
    ups: IpcUpsInfo | None


# ---------------------------------------------------------------------------
# HardwareInspector types — NC / motion
# ---------------------------------------------------------------------------


@dataclass
class AxisState:
    """Live state of one TwinCAT NC axis (read from AMS port 500)."""

    id: int
    name: str
    error_code: int
    delayed_error_code: int
    position: float
    velocity: float
    lag_error: float
    state_name: str  # "Standstill" | "Moving" | "Error" | "Unknown"


# ---------------------------------------------------------------------------
# HardwareInspector types — hardware topology (COM/XAE)
# ---------------------------------------------------------------------------


@dataclass
class TerminalInfo:
    """One EtherCAT terminal or coupler in the bus topology."""

    slot: int
    name: str          # full tree name, e.g., "Box 1 (EL1008)"
    order_number: str  # extracted order number, e.g., "EL1008"


@dataclass
class EtherCatSegment:
    """One EtherCAT master and its connected terminals."""

    master_name: str
    terminals: list[TerminalInfo]


@dataclass
class HardwareTopology:
    """Hardware topology of the open TwinCAT project (via COM/XAE)."""

    segments: list[EtherCatSegment]
    scan_timestamp: str
