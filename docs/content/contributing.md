# Contributing

## Adding a new adapter

1. **Create the file** in the correct `adapters/` subdirectory:
   ```
   tckit/adapters/readers/my_reader.py
   ```

2. **Import only from `tckit.ports` and stdlib.** Never from other adapters.

3. **Implement all abstract methods** from the port:
   ```python
   from tckit.ports.reader import ProjectReader
   from tckit.ports.types import GVL, POUInterface, POUItem, ProjectStructure

   class MyReader(ProjectReader):
       def get_structure(self, project_path: str) -> ProjectStructure: ...
       def get_pou_interface(self, pou_name: str) -> POUInterface: ...
       def get_pou_item(self, pou_name: str, item_name: str) -> POUItem: ...
       def get_gvl(self, gvl_name: str) -> GVL: ...
   ```

4. **Register it** in `tckit/config.py`:
   ```python
   _READER_REGISTRY["my_reader"] = MyReader
   ```

5. **Add the config name** to `config.example.json`:
   ```json
   { "reader": "my_reader" }
   ```

6. **Write unit tests** in `tests/unit/`.

7. **Document it** in `docs/content/adapters/my-reader.md`.

## Code style

- Python 3.11+, type hints everywhere
- Run `ruff check tckit/` before committing
- RST-format docstrings on all public methods (see `CLAUDE.md`)
- No cross-adapter imports — this is enforced by CI

## Running the test suite

```bash
docker compose -f docker/docker-compose.yml run tckit pytest tests/ -v
```

## Submitting a PR

1. Fork the repo
2. Create a branch: `git checkout -b feat/my-adapter`
3. Make your changes
4. Ensure `ruff check tckit/` and `pytest tests/unit/` both pass
5. Open a pull request with a description of what the adapter does and how to test it
