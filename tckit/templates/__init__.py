"""Package data shipped with tckit (config templates, CLAUDE.md template)."""

from __future__ import annotations

from importlib import resources
from pathlib import Path

_TWINCAT_TOPIC_FILES: tuple[str, ...] = (
    "cyclic-in-method.md",
    "polymorphism-arrays.md",
    "tcunit-tests.md",
    "naming.md",
    "comments.md",
    "multi-plc-libraries.md",
)


def install_twincat_claude_md(
    target_root: Path | str, *, overwrite: bool = False
) -> list[Path]:
    """Copy the twincat-claude.md template tree into ``target_root``.

    Lays down ``CLAUDE.md`` (from the bundled ``twincat-claude.md``)
    plus a ``twincat/`` directory of topic files. Existing files are
    not overwritten unless ``overwrite=True``; skipped files are not
    included in the returned list.

    Returns the list of files written, in the order written.
    """
    root = Path(target_root)
    root.mkdir(parents=True, exist_ok=True)
    (root / "twincat").mkdir(parents=True, exist_ok=True)

    files_to_write: list[tuple[Path, str]] = []

    linker_src = resources.files("tckit.templates").joinpath(
        "twincat-claude.md"
    )
    files_to_write.append(
        (root / "CLAUDE.md", linker_src.read_text(encoding="utf-8"))
    )

    for filename in _TWINCAT_TOPIC_FILES:
        src = resources.files("tckit.templates").joinpath("twincat", filename)
        files_to_write.append(
            (root / "twincat" / filename, src.read_text(encoding="utf-8"))
        )

    written: list[Path] = []
    for dst, content in files_to_write:
        if dst.exists() and not overwrite:
            continue
        dst.write_text(content, encoding="utf-8")
        written.append(dst)
    return written
