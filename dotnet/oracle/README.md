# Parity oracle

The Python TcKit is the golden master for the rewrite. For every ported tool, run
the same operation through both servers against the same fixture project and diff
the result. The MCP contract is preserved (ADR-0015), so the diff is direct: same
tool name, same parameters, same expected output shape.

Two diff levels:

- **Tool output**, compare the JSON a tool returns (readers, docs, hardware
  reads).
- **Project artefact**, for authoring tools, compare the resulting `.TcPOU` /
  `.plcproj` XML the operation writes into the project tree, normalised so
  volatile fields (GUIDs, timestamps) do not cause false diffs.

[`compare.ps1`](compare.ps1) is the harness skeleton: it fixes the flow (run
both, normalise, diff) and gets filled in as the C# tools come online. Until
then it documents the intended shape.

Progress is tracked in [../PORTING.md](../PORTING.md).
