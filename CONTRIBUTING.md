# Contributing to TcKit

## Adding a new adapter

1. **Create** `tckit/adapters/{port_type}s/{name}_{port_type}.py`
2. **Import only** from `tckit.ports` and stdlib — never from other adapters
3. **Implement** all abstract methods from the port ABC
4. **Register** the class in `tckit/config.py` under the appropriate registry
5. **Add** the config key to `config.example.json`
6. **Write** unit tests in `tests/unit/`
7. **Document** in `docs/content/capabilities/<port>/<adapter>.md` and add it to `docs/mkdocs.yml`

Naming convention: `{tool}_{port_type}` — e.g. `xml_reader`, `xae_com_builder`.

## Adding a new port

Only do this if there is a genuinely new external concern — not a variation of an existing one. Open an issue first to discuss before implementing.

1. Define the ABC in `tckit/ports/`
2. Keep method signatures minimal
3. Return types must be dataclasses defined in `tckit/ports/types.py`
4. Update `tckit/server.py` to expose the port as MCP tools
5. Implement at least one adapter before merging

## Code style

- Python 3.11+, type hints everywhere, `strict` mypy
- Run `ruff check tckit/` before committing — CI will reject lint failures
- RST-format docstrings on all public methods
- No comments explaining what the code does — only why (non-obvious constraints, workarounds)

## Running the full check locally

```bash
docker compose -f docker/docker-compose.yml run tckit ruff check tckit/
python scripts/check-adapter-isolation.py
docker compose -f docker/docker-compose.yml run tckit pytest tests/unit/ -v
```

## Opening a pull request

- Branch from `main`, name: `feat/`, `fix/`, `chore/`
- One logical change per PR
- Unit tests must pass
- Include a description of what the adapter/feature does and how you tested it
