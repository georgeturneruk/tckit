# blark reader

**Port:** `ProjectReader`  
**Module:** `tckit.adapters.readers.blark_reader.BlarkReader`  
**Status:** Phase 1 — in progress

Uses the [blark](https://github.com/klauer/blark) Python library to parse `.TcPOU` and `.TcGVL` files. Runs entirely in Docker — no XAE or Windows dependency.

## Configuration

```json
{ "reader": "blark" }
```

No additional configuration required.

## Known limitations

- blark grammar gaps may cause parse failures on certain project patterns. Validate against your project files early.
- If blark cannot parse a file, open a GitHub issue with the anonymised `.TcPOU` file attached.
