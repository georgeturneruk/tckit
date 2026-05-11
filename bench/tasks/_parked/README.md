# Parked benchmark tasks

Tasks in this directory are kept on disk for context but are not part of the active bench run.

A task gets parked when it exercises a workflow TcKit does not currently support, so running it produces no signal about TcKit's effectiveness; both configs fall back to the same stock-tool path and the comparison is noise. Parking is reversible: when TcKit grows the relevant capability the task can be moved back into `bench/tasks/`.

## Currently parked

- `02-find-callers.md` — exhaustive find-callers across all `.TcPOU` files. ADR-0001 (`Exploring`) discusses a `ProjectSearcher` port that would put TcKit on the field for this workflow; until then both configs default to `Grep` and the bench learns nothing.
