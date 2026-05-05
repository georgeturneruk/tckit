#!/usr/bin/env python3
"""validate-xml-reader.py — manual spike to validate XmlReader against a real project.

Usage:
    python scripts/validate-xml-reader.py /path/to/plc/project

Prints a summary of discovered POUs, GVLs, and a sample interface/item.
"""

import sys
from pathlib import Path

# Allow running from repo root without installing the package
sys.path.insert(0, str(Path(__file__).parent.parent))

from tckit.adapters.readers.xml_reader import XmlReader


def main() -> None:
    if len(sys.argv) < 2:
        project_path = str(Path(__file__).parent.parent / "tests" / "fixtures" / "sample_project")
        print(f"No path supplied — using fixture: {project_path}\n")
    else:
        project_path = sys.argv[1]

    reader = XmlReader()

    print(f"=== get_structure({project_path!r}) ===")
    structure = reader.get_structure(project_path)
    print(f"  POUs ({len(structure.pous)}):")
    for pou in structure.pous:
        print(f"    {pou.pou_type:20s}  {pou.name}")
    print(f"  GVLs ({len(structure.gvls)}): {structure.gvls}")
    print()

    if structure.pous:
        first_pou = structure.pous[0].name
        print(f"=== get_pou_interface({first_pou!r}) ===")
        interface = reader.get_pou_interface(first_pou)
        print(f"  pou_type:    {interface.pou_type}")
        print(f"  declaration: {interface.declaration[:120]!r}...")
        print(f"  methods ({len(interface.methods)}):")
        for m in interface.methods:
            print(f"    {m.name} : {m.return_type}")
        print(f"  actions:    {interface.actions}")
        print(f"  properties: {interface.properties}")
        print()

        if interface.methods:
            first_method = interface.methods[0].name
            print(f"=== get_pou_item({first_pou!r}, {first_method!r}) ===")
            item = reader.get_pou_item(first_pou, first_method)
            print(f"  declaration: {item.declaration[:120]!r}...")
            print(f"  body:        {item.body[:120]!r}...")
            print()

    if structure.gvls:
        first_gvl = structure.gvls[0]
        print(f"=== get_gvl({first_gvl!r}) ===")
        gvl = reader.get_gvl(first_gvl)
        print(f"  declaration: {gvl.declaration[:200]!r}...")

    print("\nAll checks passed.")


if __name__ == "__main__":
    main()
