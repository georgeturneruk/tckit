# sphinx generator

**Port:** `DocGenerator`  
**Module:** `tckit.adapters.doc_generators.sphinx_generator.SphinxGenerator`  
**Status:** Phase 1 — in progress

Generates HTML documentation from RST-commented ST source using plcdoc + Sphinx. Runs entirely in Docker.

## Configuration

```json
{
  "doc_generator": "sphinx",
  "doc_trigger": "on_build",
  "docs_output_path": "./docs/plc"
}
```

## Comment style

Always write RST-format comments in ST code:

```pascal
// :Description: Reads an SDO value from an EtherCAT slave
// :param sNetId: AMS Net ID of the EtherCAT master
// :returns: TRUE if read successful
METHOD ReadSDO : BOOL
```

This aligns with Beckhoff TE1030 and feeds into the Sphinx pipeline.
