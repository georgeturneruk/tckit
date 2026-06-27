"""bridge_hardware_adapter — HardwareInspector via Windows bridge → TcAdsDll raw ADS reads."""

from __future__ import annotations

from typing import Any

from tckit.ports.hardware_inspector import HardwareInspector
from tckit.ports.types import (
    AxisState,
    EtherCatMasterInfo,
    EtherCatMasterState,
    EtherCatSlaveInfo,
    EtherCatStatus,
    IpcCpuInfo,
    IpcFanInfo,
    IpcHardware,
    IpcMemoryInfo,
    IpcNicInfo,
    IpcUpsInfo,
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

    def list_axes(self, target_ams_id: str) -> list[AxisState]:
        payload: dict[str, Any] = {"TargetAmsId": target_ams_id}
        try:
            resp = self._client.post("/nc-axes", payload)
        except BridgeError as exc:
            raise RuntimeError(str(exc)) from exc
        if not resp.get("success"):
            raise RuntimeError(resp.get("error") or "Bridge returned failure for /nc-axes")
        return [_to_axis_state(a) for a in (resp.get("axes") or [])]

    def get_axis_state(self, target_ams_id: str, axis_id: int) -> AxisState:
        payload: dict[str, Any] = {"TargetAmsId": target_ams_id, "AxisId": axis_id}
        try:
            resp = self._client.post("/nc-axes", payload)
        except BridgeError as exc:
            raise RuntimeError(str(exc)) from exc
        if not resp.get("success"):
            raise RuntimeError(resp.get("error") or "Bridge returned failure for /nc-axes")
        axes = resp.get("axes") or []
        if not axes:
            raise RuntimeError(f"Axis {axis_id} not found")
        return _to_axis_state(axes[0])

    def get_ipc_hardware(self, target_ams_id: str) -> IpcHardware:
        payload: dict[str, Any] = {"TargetAmsId": target_ams_id}
        try:
            resp = self._client.post("/ipc-hardware", payload)
        except BridgeError as exc:
            raise RuntimeError(str(exc)) from exc
        if not resp.get("success"):
            raise RuntimeError(resp.get("error") or "Bridge returned failure for /ipc-hardware")
        return _to_ipc_hardware(resp)

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


def _to_ipc_hardware(resp: dict[str, Any]) -> IpcHardware:
    cpu_raw = resp.get("cpu")
    mem_raw = resp.get("memory")
    ups_raw = resp.get("ups")
    return IpcHardware(
        twincat_version=resp.get("twincat_version") or None,
        cpu=_to_cpu_info(cpu_raw) if cpu_raw else None,
        memory=_to_memory_info(mem_raw) if mem_raw else None,
        fans=[_to_fan_info(i, f) for i, f in enumerate(resp.get("fans") or [])],
        nics=[_to_nic_info(i, n) for i, n in enumerate(resp.get("nics") or [])],
        ups=_to_ups_info(ups_raw) if ups_raw else None,
    )


def _to_cpu_info(raw: dict[str, Any]) -> IpcCpuInfo:
    temp = raw.get("temperature_c")
    return IpcCpuInfo(
        temperature_c=int(temp) if temp is not None else None,
        usage_pct=int(raw.get("usage_pct", 0)),
        frequency_mhz=int(raw.get("frequency_mhz", 0)),
    )


def _to_memory_info(raw: dict[str, Any]) -> IpcMemoryInfo:
    return IpcMemoryInfo(
        total_mb=int(raw.get("total_mb", 0)),
        free_mb=int(raw.get("free_mb", 0)),
    )


def _to_fan_info(index: int, raw: dict[str, Any]) -> IpcFanInfo:
    return IpcFanInfo(index=index, rpm=int(raw.get("rpm", 0)))


def _to_nic_info(index: int, raw: dict[str, Any]) -> IpcNicInfo:
    return IpcNicInfo(
        index=index,
        mac=str(raw.get("mac", "")),
        ipv4=str(raw.get("ipv4", "")),
    )


def _to_ups_info(raw: dict[str, Any]) -> IpcUpsInfo:
    return IpcUpsInfo(
        battery_pct=int(raw.get("battery_pct", 0)),
        power_ok=bool(raw.get("power_ok", True)),
        battery_ok=bool(raw.get("battery_ok", True)),
        power_fail_count=int(raw.get("power_fail_count", 0)),
    )


def _to_axis_state(raw: dict[str, Any]) -> AxisState:
    return AxisState(
        id=int(raw.get("id", 0)),
        name=str(raw.get("name", "")),
        error_code=int(raw.get("error_code", 0)),
        delayed_error_code=int(raw.get("delayed_error_code", 0)),
        position=float(raw.get("position", 0.0)),
        velocity=float(raw.get("velocity", 0.0)),
        lag_error=float(raw.get("lag_error", 0.0)),
        state_name=str(raw.get("state_name", "Unknown")),
    )
