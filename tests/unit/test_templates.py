"""Tests for tckit.templates — install_twincat_claude_md helper."""

from __future__ import annotations

from importlib import resources
from pathlib import Path

import pytest

from tckit.templates import _TWINCAT_TOPIC_FILES, install_twincat_claude_md


def test_install_writes_linker_and_all_topics(tmp_path: Path) -> None:
    written = install_twincat_claude_md(tmp_path)

    expected_relative = {"CLAUDE.md", *(f"twincat/{f}" for f in _TWINCAT_TOPIC_FILES)}
    written_relative = {str(p.relative_to(tmp_path)).replace("\\", "/") for p in written}
    assert written_relative == expected_relative

    assert (tmp_path / "CLAUDE.md").exists()
    for filename in _TWINCAT_TOPIC_FILES:
        assert (tmp_path / "twincat" / filename).exists()


def test_install_linker_contains_pointers_to_topics(tmp_path: Path) -> None:
    install_twincat_claude_md(tmp_path)
    body = (tmp_path / "CLAUDE.md").read_text(encoding="utf-8")
    for filename in _TWINCAT_TOPIC_FILES:
        assert f"twincat/{filename}" in body, (
            f"linker should reference {filename}"
        )


def test_install_refuses_overwrite_by_default(tmp_path: Path) -> None:
    (tmp_path / "CLAUDE.md").write_text("# existing user content\n", encoding="utf-8")

    written = install_twincat_claude_md(tmp_path)

    assert (tmp_path / "CLAUDE.md").read_text(encoding="utf-8") == (
        "# existing user content\n"
    )
    assert (tmp_path / "CLAUDE.md") not in written
    # Topic files (none of which existed) should still be laid down.
    for filename in _TWINCAT_TOPIC_FILES:
        assert (tmp_path / "twincat" / filename) in written


def test_install_overwrites_when_requested(tmp_path: Path) -> None:
    (tmp_path / "CLAUDE.md").write_text("# stale\n", encoding="utf-8")

    written = install_twincat_claude_md(tmp_path, overwrite=True)

    body = (tmp_path / "CLAUDE.md").read_text(encoding="utf-8")
    assert "TwinCAT conventions" in body
    assert (tmp_path / "CLAUDE.md") in written


def test_install_creates_missing_subdirectories(tmp_path: Path) -> None:
    target = tmp_path / "fresh" / "nested"
    written = install_twincat_claude_md(target)
    assert target.exists()
    assert (target / "twincat").is_dir()
    assert len(written) == 1 + len(_TWINCAT_TOPIC_FILES)


def test_topic_files_are_packaged() -> None:
    """The new template files must be reachable as package resources."""
    linker = (
        resources.files("tckit.templates")
        .joinpath("twincat-claude.md")
        .read_text(encoding="utf-8")
    )
    assert "TwinCAT conventions" in linker

    for filename in _TWINCAT_TOPIC_FILES:
        body = (
            resources.files("tckit.templates")
            .joinpath("twincat", filename)
            .read_text(encoding="utf-8")
        )
        assert body.startswith("#"), f"{filename} should start with a heading"


def test_topic_files_are_byte_identical_to_installed_copies(tmp_path: Path) -> None:
    install_twincat_claude_md(tmp_path)
    for filename in _TWINCAT_TOPIC_FILES:
        packaged = (
            resources.files("tckit.templates")
            .joinpath("twincat", filename)
            .read_text(encoding="utf-8")
        )
        installed = (tmp_path / "twincat" / filename).read_text(encoding="utf-8")
        assert packaged == installed, f"{filename} drifted on copy"


@pytest.mark.parametrize("kind", ["str", "path"])
def test_install_accepts_str_or_path(tmp_path: Path, kind: str) -> None:
    target = str(tmp_path) if kind == "str" else tmp_path
    written = install_twincat_claude_md(target)
    assert (tmp_path / "CLAUDE.md") in written
