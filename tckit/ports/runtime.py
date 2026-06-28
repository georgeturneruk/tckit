"""RuntimeAdapter port — ADS runtime control and symbol I/O."""

from __future__ import annotations

from abc import ABC, abstractmethod
from typing import Any

from tckit.ports.types import Result


class RuntimeAdapter(ABC):
    """ADS runtime control, symbol reads/writes, and RPC method invocation.

    All operations target a running TwinCAT runtime identified by its
    AMS Net ID. Symbol operations require the runtime to already be in
    Run mode; use the builder's deploy() first if needed.

    start_runtime is target-wide (no plc_name). Symbol paths address
    individual instances regardless of which PLC project owns them.
    """

    @abstractmethod
    def start_runtime(self, target_ams_id: str) -> Result:
        """Start or restart the TwinCAT runtime on a target.

        :param target_ams_id: AMS Net ID of the target (e.g.
            ``192.168.1.100.1.1``).
        """
        ...

    @abstractmethod
    def read_symbols(
        self, target_ams_id: str, paths: list[str]
    ) -> dict[str, str | None]:
        """Read PLC symbols by instance path on a running runtime.

        Best-effort: an unreadable symbol does not fail the call — it
        maps to ``None`` in the returned dict.

        :param target_ams_id: AMS Net ID of the target.
        :param paths: Symbol instance paths (e.g.
            ``["MAIN.nCounter", "GVL.bEnable"]``). Empty list returns
            an empty dict.
        :returns: Mapping of path -> string value (or ``None`` when the
            path couldn't be resolved on the runtime).
        """
        ...

    @abstractmethod
    def write_symbols(
        self,
        target_ams_id: str,
        writes: dict[str, Any],
    ) -> Result:
        """Write PLC symbols by instance path on a running runtime.

        Best-effort: per-symbol failures land in
        ``Result.details["errors"]`` keyed by path and do not abort
        remaining writes. ``Result.success`` is ``True`` only when
        every write succeeded.

        Type resolution is handled bridge-side by TcXaeMgmt, which
        queries ADS for each symbol's declared type and coerces the
        supplied value. Pass values that are compatible with the
        declared PLC type (e.g. Python ``int`` for an ``INT`` symbol).

        Supported value types: any JSON-serialisable scalar (int,
        float, bool, str), 1-D lists for ARRAY symbols, and dicts for
        STRUCT symbols whose PLC declaration carries
        ``{attribute 'pack_mode' := '1'}``.

        :param target_ams_id: AMS Net ID of the target.
        :param writes: Mapping of symbol instance path -> value to
            write (e.g. ``{"MAIN.nSetpoint": 42, "GVL.bEnable": True}``).
        :returns: Result with ``details["written"]`` (paths that
            succeeded) and ``details["errors"]`` (paths that failed,
            with the error message as the value).
        """
        ...

    @abstractmethod
    def invoke_rpc(
        self,
        target_ams_id: str,
        symbol_path: str,
        method_name: str,
        params: list[Any] | None = None,
    ) -> Result:
        """Invoke a PLC method decorated with ``{attribute 'TcRpcEnable'}``.

        The method must be on a FB instance (not a PROGRAM's local
        function). Parameters are positional and must match the
        method's ``VAR_INPUT`` declaration order.

        :param target_ams_id: AMS Net ID of the target.
        :param symbol_path: Instance path of the FB that owns the
            method (e.g. ``"MAIN.fbPid"``; use ``"MAIN"`` for methods
            directly on the MAIN program).
        :param method_name: Method name as declared in the PLC
            (e.g. ``"M_Reset"``).
        :param params: Positional parameters matching the method's
            ``VAR_INPUT`` order. ``None`` is equivalent to ``[]``.
        :returns: Result with ``details["return_value"]`` and
            ``details["return_type"]`` populated when the method has a
            non-void return type.
        """
        ...
