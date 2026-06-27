"""HardwareInspector port — read-only hardware diagnostics for a running TwinCAT system."""

from abc import ABC, abstractmethod

from tckit.ports.types import AxisState, EtherCatMasterInfo, EtherCatStatus, IpcHardware


class HardwareInspector(ABC):
    """Read-only inspection of TwinCAT hardware from a running runtime.

    All methods target a live TwinCAT system via ADS; the system must be
    in Config or Run mode. No XAE is required (pure ADS, no COM).

    :param target_ams_id: AMS Net ID of the TwinCAT system to inspect
        (e.g. ``192.168.1.100.1.1``). Passed per-call.
    """

    @abstractmethod
    def list_ethercat_masters(self, target_ams_id: str) -> list[EtherCatMasterInfo]:
        """Return every EtherCAT master found on the target system.

        Probes AMS port 65535 (0xFFFF) on the target. A single EtherCAT
        master is the common case; the list will have exactly one entry
        for most TwinCAT 3 installations.

        :param target_ams_id: AMS Net ID of the target.
        :returns: List of master info structs (empty if no master found).
        """
        ...

    @abstractmethod
    def get_ethercat_status(
        self,
        target_ams_id: str,
        master_net_id: str = "",
    ) -> EtherCatStatus:
        """Read the full EtherCAT status snapshot for one master.

        Returns master-level diagnostic flags and the complete slave table
        with state-machine states, identity (vendor/product/revision/serial),
        link health, and per-port CRC error counters.

        :param target_ams_id: AMS Net ID of the target system.
        :param master_net_id: AMS Net ID of the EtherCAT master. When empty,
            defaults to ``target_ams_id`` (the typical single-master layout
            where the master lives on the same AMS node as the system).
        :returns: :class:`EtherCatStatus` with master state and slave list.
        """
        ...

    @abstractmethod
    def get_ipc_hardware(self, target_ams_id: str) -> IpcHardware:
        """Read IPC hardware diagnostics from a running TwinCAT system.

        Reads all discovered MDP modules via AMS port 10000 (SystemService):
        CPU (temperature, utilisation, frequency), memory (total/free),
        fans (RPM per fan), network adapters (MAC, IPv4), UPS (battery,
        power status), and the TwinCAT runtime version.

        Modules not present on the system are returned as ``None`` or empty
        lists. Individual property reads that fail (e.g. CPU temperature
        requires BIOS API support) return ``None`` for that field.

        :param target_ams_id: AMS Net ID of the target system.
        :returns: :class:`IpcHardware` snapshot of all found MDP modules.
        """
        ...

    @abstractmethod
    def list_axes(self, target_ams_id: str) -> list[AxisState]:
        """Enumerate all configured NC axes and return their live state.

        Reads axis IDs from the NC Ring0 manager (AMS port 500, IG 0x1100),
        then reads name, error code, position, velocity, and lag error per
        axis.  Returns an empty list when no NC axes are configured.

        :param target_ams_id: AMS Net ID of the target system.
        :returns: One :class:`AxisState` per configured axis.
        """
        ...

    @abstractmethod
    def get_axis_state(self, target_ams_id: str, axis_id: int) -> AxisState:
        """Read the live state of a single NC axis.

        :param target_ams_id: AMS Net ID of the target system.
        :param axis_id: Axis ID as returned by :meth:`list_axes`.
        :returns: :class:`AxisState` for the requested axis.
        :raises RuntimeError: If the axis ID does not exist.
        """
        ...
