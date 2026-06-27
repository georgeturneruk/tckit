"""bridge_hardware_adapter — HardwareInspector via Windows bridge → TcAdsDll raw ADS reads."""

from __future__ import annotations

from typing import Any

from tckit.ports.hardware_inspector import HardwareInspector
from tckit.ports.types import (
    EtherCatMasterInfo,
    EtherCatMasterState,
    EtherCatSlaveInfo,
    EtherCatStatus,
)
from tckit.utils.bridge_client import BridgeClient, BridgeError

_ETHERCAT_MASTER_PORT = 65535  # 0xFFFF


class BridgeHardwareAdapter(HardwareInspector):
    """Reads TwinCAT hardware diagnostics via raw ADS through the Windows bridge.

    The bridge harness scripts use TcAdsDll.dll (native Win32, available on
    any TwinCAT 3 install) for raw index-group reads at system AMS ports
    (EtherCAT master port 0xFFFF, etc.). No XAE required; only needs the
    TwinCAT runtime to be reachable via ADS.
    """

    def __init__(self, client: BridgeClient | None = None) -> None:
        self._client = client or BridgeClient()

    # ------------------------------------------------------------------
    # HardwareInspector interface
    # ------------------------------------------------------------------

    def list_ethercat_masters(self, target_ams_id: str) -> list[EtherCatMasterInfo]:
        payload: dict[str, Any] = {
            "TargetAmsId": target_ams_id,
            "ListMastersOnly": True,
        }
        try:
            resp = self._client.post("/ethercat-status", payload)
        except BridgeError as exc:
            raise RuntimeError(str(exc)) from exc
        if not resp.get("success"):
            raise RuntimeError(resp.get("error") or "Bridge returned failure for /ethercat-status")
        return [_to_master_info(m) for m in (resp.get("masters") or [])]

    def get_ethercat_status(
        self,
        target_ams_id: str,
        master_net_id: str = "",
    ) -> EtherCatStatus:
        payload: dict[str, Any] = {
            "TargetAmsId": target_ams_id,
            "MasterNetId": master_net_id or target_ams_id,
        }
        try:
            resp = self._client.post("/ethercat-status", payload)
        except BridgeError as exc:
            raise RuntimeError(str(exc)) from exc
        if not resp.get("success"):
            raise RuntimeError(resp.get("error") or "Bridge returned failure for /ethercat-status")
        return _to_ethercat_status(resp)


# ---------------------------------------------------------------------------
# Response → dataclass mappers
# ---------------------------------------------------------------------------


def _to_master_info(raw: dict[str, Any]) -> EtherCatMasterInfo:
    return EtherCatMasterInfo(
        net_id=str(raw.get("net_id", "")),
        name=str(raw.get("name", "EtherCAT Master")),
        port=int(raw.get("port", _ETHERCAT_MASTER_PORT)),
    )


def _to_slave_info(raw: dict[str, Any]) -> EtherCatSlaveInfo:
    return EtherCatSlaveInfo(
        address=int(raw.get("address", 0)),
        name=str(raw.get("name", "")),
        vendor_id=int(raw.get("vendor_id", 0)),
        product_code=int(raw.get("product_code", 0)),
        revision=int(raw.get("revision", 0)),
        serial=int(raw.get("serial", 0)),
        state=str(raw.get("state", "UNKNOWN")),
        link_ok=bool(raw.get("link_ok", False)),
        crc_errors_a=int(raw.get("crc_errors_a", 0)),
        crc_errors_b=int(raw.get("crc_errors_b", 0)),
        crc_errors_c=int(raw.get("crc_errors_c", 0)),
        crc_errors_d=int(raw.get("crc_errors_d", 0)),
    )


def _to_master_state(raw: dict[str, Any]) -> EtherCatMasterState:
    flags = int(raw.get("state_flags", 0))
    return EtherCatMasterState(
        state_flags=flags,
        link_error=bool(raw.get("link_error", False)),
        io_locked=bool(raw.get("io_locked", False)),
        watchdog_triggered=bool(raw.get("watchdog_triggered", False)),
        dc_out_of_sync=bool(raw.get("dc_out_of_sync", False)),
    )


def _to_ethercat_status(resp: dict[str, Any]) -> EtherCatStatus:
    master_raw = resp.get("master") or {}
    slaves_raw = resp.get("slaves") or []
    return EtherCatStatus(
        master=_to_master_state(master_raw),
        slaves=[_to_slave_info(s) for s in slaves_raw],
    )
