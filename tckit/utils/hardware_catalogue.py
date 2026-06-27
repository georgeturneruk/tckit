"""Bundled catalogue of common Beckhoff EtherCAT terminal I/O channels.

Maps order number → list of (channel_name, data_type, direction) tuples.
Direction is "IN" (input to PLC) or "OUT" (output from PLC).
Terminals not listed are treated as unknown (scaffold generates a comment).

This catalogue covers the most common digital/analog terminals.  For
exhaustive I/O information, use the Beckhoff infosys docs searcher.
"""

from __future__ import annotations

# (channel_name, st_type, direction)
Channel = tuple[str, str, str]

CATALOGUE: dict[str, list[Channel]] = {
    # ── Couplers / power supplies (no process I/O) ──────────────────────────
    "EK1100": [],
    "EK1101": [],
    "EK1110": [],
    "EK1122": [],
    "EK1501": [],
    "EK9000": [],
    "EK9300": [],
    "EL9505": [],
    "EL9410": [],
    "EL9560": [],
    # ── Digital inputs ───────────────────────────────────────────────────────
    "EL1002": [("Input_1", "BOOL", "IN"), ("Input_2", "BOOL", "IN")],
    "EL1004": [(f"Input_{i}", "BOOL", "IN") for i in range(1, 5)],
    "EL1008": [(f"Input_{i}", "BOOL", "IN") for i in range(1, 9)],
    "EL1012": [(f"Input_{i}", "BOOL", "IN") for i in range(1, 3)],
    "EL1014": [(f"Input_{i}", "BOOL", "IN") for i in range(1, 5)],
    "EL1018": [(f"Input_{i}", "BOOL", "IN") for i in range(1, 9)],
    "EL1024": [(f"Input_{i}", "BOOL", "IN") for i in range(1, 5)],
    "EL1034": [(f"Input_{i}", "BOOL", "IN") for i in range(1, 5)],
    "EL1088": [(f"Input_{i}", "BOOL", "IN") for i in range(1, 9)],
    # ── Digital outputs ──────────────────────────────────────────────────────
    "EL2002": [("Output_1", "BOOL", "OUT"), ("Output_2", "BOOL", "OUT")],
    "EL2004": [(f"Output_{i}", "BOOL", "OUT") for i in range(1, 5)],
    "EL2008": [(f"Output_{i}", "BOOL", "OUT") for i in range(1, 9)],
    "EL2024": [(f"Output_{i}", "BOOL", "OUT") for i in range(1, 5)],
    "EL2034": [(f"Output_{i}", "BOOL", "OUT") for i in range(1, 5)],
    "EL2088": [(f"Output_{i}", "BOOL", "OUT") for i in range(1, 9)],
    "EL2124": [(f"Output_{i}", "BOOL", "OUT") for i in range(1, 5)],
    # ── Mixed digital I/O ────────────────────────────────────────────────────
    "EL1809": [(f"Input_{i}", "BOOL", "IN") for i in range(1, 17)],
    "EL2809": [(f"Output_{i}", "BOOL", "OUT") for i in range(1, 17)],
    # ── Analog inputs ────────────────────────────────────────────────────────
    "EL3001": [("Analog_Input_1", "INT", "IN")],
    "EL3002": [(f"Analog_Input_{i}", "INT", "IN") for i in range(1, 3)],
    "EL3004": [(f"Analog_Input_{i}", "INT", "IN") for i in range(1, 5)],
    "EL3008": [(f"Analog_Input_{i}", "INT", "IN") for i in range(1, 9)],
    "EL3102": [(f"Analog_Input_{i}", "INT", "IN") for i in range(1, 3)],
    "EL3104": [(f"Analog_Input_{i}", "INT", "IN") for i in range(1, 5)],
    "EL3112": [(f"Analog_Input_{i}", "INT", "IN") for i in range(1, 3)],
    "EL3152": [(f"Analog_Input_{i}", "INT", "IN") for i in range(1, 3)],
    "EL3162": [(f"Analog_Input_{i}", "INT", "IN") for i in range(1, 3)],
    # ── Analog outputs ───────────────────────────────────────────────────────
    "EL4001": [("Analog_Output_1", "INT", "OUT")],
    "EL4002": [(f"Analog_Output_{i}", "INT", "OUT") for i in range(1, 3)],
    "EL4004": [(f"Analog_Output_{i}", "INT", "OUT") for i in range(1, 5)],
    "EL4008": [(f"Analog_Output_{i}", "INT", "OUT") for i in range(1, 9)],
    "EL4012": [(f"Analog_Output_{i}", "INT", "OUT") for i in range(1, 3)],
    "EL4032": [(f"Analog_Output_{i}", "INT", "OUT") for i in range(1, 3)],
    "EL4132": [(f"Analog_Output_{i}", "INT", "OUT") for i in range(1, 3)],
    # ── Encoder / position ───────────────────────────────────────────────────
    "EL5001": [("Position", "DWORD", "IN"), ("Status", "WORD", "IN")],
    "EL5002": [
        ("Position_1", "DWORD", "IN"), ("Status_1", "WORD", "IN"),
        ("Position_2", "DWORD", "IN"), ("Status_2", "WORD", "IN"),
    ],
    "EL5101": [("Position", "DWORD", "IN"), ("Velocity", "INT", "IN")],
    "EL5151": [("Position", "DWORD", "IN"), ("Velocity", "INT", "IN")],
    # ── Serial / fieldbus ────────────────────────────────────────────────────
    "EL6001": [("Data_In", "BYTE", "IN"), ("Data_Out", "BYTE", "OUT")],
    "EL6002": [("Data_In_1", "BYTE", "IN"), ("Data_Out_1", "BYTE", "OUT")],
    # ── Safety ──────────────────────────────────────────────────────────────
    "EL1904": [(f"Safety_Input_{i}", "BOOL", "IN") for i in range(1, 5)],
    "EL2904": [(f"Safety_Output_{i}", "BOOL", "OUT") for i in range(1, 5)],
}


def lookup(order_number: str) -> list[Channel] | None:
    """Return channel list for *order_number*, or ``None`` if unknown.

    The lookup is case-insensitive and ignores any suffix after the first
    space (so "EL1008-0000" and "EL1008 0000" both resolve to "EL1008").
    Returns an empty list (not ``None``) for terminals with no process I/O.
    """
    key = order_number.strip().upper().split()[0].split("-")[0]
    return CATALOGUE.get(key)
