"""XaeComRuntime — RuntimeAdapter via Windows bridge -> TcXaeMgmt / .NET ADS."""

from __future__ import annotations

import json
from typing import Any

from tckit.ports.runtime import RuntimeAdapter
from tckit.ports.types import Result
from tckit.utils.bridge_client import BridgeClient, BridgeError


class XaeComRuntime(RuntimeAdapter):
    """RuntimeAdapter backed by the TcXaeMgmt PowerShell module via the bridge.

    ``write_symbols`` and ``invoke_rpc`` are routed through dedicated
    bridge harness scripts (``Write-TcSymbol.ps1`` /
    ``Invoke-TcRpcMethod.ps1``). The confirmed gate for destructive
    operations lives in ``server.py``; this adapter is unconditional.
    """

    def __init__(self, client: BridgeClient | None = None) -> None:
        self._client = client or BridgeClient()

    # ------------------------------------------------------------------
    # RuntimeAdapter interface
    # ------------------------------------------------------------------

    def start_runtime(self, target_ams_id: str) -> Result:
        payload: dict[str, Any] = {
            "TargetAmsId": target_ams_id,
            "Mode": "Run",
            "Wait": True,
        }
        try:
            resp = self._client.post("/runtime", payload)
        except BridgeError as exc:
            return Result(success=False, error=str(exc))
        return _to_result(resp)

    def read_symbols(
        self, target_ams_id: str, paths: list[str]
    ) -> dict[str, str | None]:
        if not paths:
            return {}
        payload = {
            "TargetAmsId": target_ams_id,
            # Newline-separated rather than a JSON array because the
            # bridge's request decoder collapses nested string arrays
            # unhelpfully on PowerShell 5.1; same convention as
            # /tcunit-run's ReadSymbols parameter.
            "Paths": "\n".join(paths),
        }
        try:
            resp = self._client.post("/symbols", payload)
        except BridgeError:
            return {p: None for p in paths}
        values = resp.get("values") if isinstance(resp, dict) else None
        if not isinstance(values, dict):
            return {p: None for p in paths}
        out: dict[str, str | None] = {}
        for path in paths:
            raw = values.get(path)
            out[path] = str(raw) if raw is not None else None
        return out

    def write_symbols(
        self,
        target_ams_id: str,
        writes: dict[str, Any],
    ) -> Result:
        if not writes:
            return Result(success=True, details={"written": {}, "errors": {}})
        payload: dict[str, Any] = {
            "TargetAmsId": target_ams_id,
            # Double-encoded JSON preserves mixed value types (int, bool,
            # list, dict) through PowerShell 5.1's ConvertFrom-Json, which
            # loses type fidelity on nested structures when they arrive as
            # top-level payload fields.
            "WritesJson": json.dumps(writes),
        }
        try:
            resp = self._client.post("/write-symbols", payload)
        except BridgeError as exc:
            return Result(success=False, error=str(exc))
        return _to_result(resp)

    def invoke_rpc(
        self,
        target_ams_id: str,
        symbol_path: str,
        method_name: str,
        params: list[Any] | None = None,
    ) -> Result:
        payload: dict[str, Any] = {
            "TargetAmsId": target_ams_id,
            "SymbolPath": symbol_path,
            "MethodName": method_name,
            # Same double-encoding rationale as WritesJson above.
            "ParamsJson": json.dumps(params or []),
        }
        try:
            resp = self._client.post("/invoke-rpc", payload)
        except BridgeError as exc:
            return Result(success=False, error=str(exc))
        return _to_result(resp)


def _to_result(resp: dict[str, Any]) -> Result:
    return Result(
        success=bool(resp.get("success", False)),
        error=resp.get("error"),
        details={k: v for k, v in resp.items() if k not in ("success", "error")},
    )
