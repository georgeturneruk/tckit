"""Tests that port ABCs cannot be instantiated directly, plus shape locks
for dataclasses shared across the MCP boundary."""

from dataclasses import asdict

import pytest

from tckit.ports.builder import BuildRunner
from tckit.ports.doc_generator import DocGenerator
from tckit.ports.docs_searcher import DocsSearcher
from tckit.ports.reader import ProjectReader
from tckit.ports.test_runner import TestRunner
from tckit.ports.types import (
    AssertFailure,
    TestCase,
    TestResults,
    TestResultsSummary,
    TestSuite,
)
from tckit.ports.writer import ProjectWriter


def test_project_reader_is_abstract() -> None:
    with pytest.raises(TypeError):
        ProjectReader()  # type: ignore[abstract]


def test_project_writer_is_abstract() -> None:
    with pytest.raises(TypeError):
        ProjectWriter()  # type: ignore[abstract]


def test_build_runner_is_abstract() -> None:
    with pytest.raises(TypeError):
        BuildRunner()  # type: ignore[abstract]


def test_test_runner_is_abstract() -> None:
    with pytest.raises(TypeError):
        TestRunner()  # type: ignore[abstract]


def test_doc_generator_is_abstract() -> None:
    with pytest.raises(TypeError):
        DocGenerator()  # type: ignore[abstract]


def test_docs_searcher_is_abstract() -> None:
    with pytest.raises(TypeError):
        DocsSearcher()  # type: ignore[abstract]


def test_test_results_default_shape_round_trips() -> None:
    """Locks in the JSON shape that crosses the MCP boundary."""
    results = TestResults(
        suites=[
            TestSuite(
                name="FB_Demo_Suite",
                tests=[
                    TestCase(name="Adds", passed=True, asserts=2),
                    TestCase(
                        name="Subtracts",
                        passed=False,
                        asserts=3,
                        failures=[
                            AssertFailure(
                                message="expected 1, got 2",
                                expected="1",
                                actual="2",
                                line=42,
                            )
                        ],
                        duration_seconds=0.01,
                    ),
                ],
            )
        ],
        summary=TestResultsSummary(
            suites=1,
            tests=2,
            asserts=5,
            failures=1,
            errors=0,
            duration_seconds=0.5,
        ),
    )

    assert results.total_passed == 1
    assert results.total_failed == 1
    assert results.success is False

    payload = asdict(results)
    assert set(payload) == {"suites", "summary"}
    assert payload["summary"]["failures"] == 1
    assert payload["suites"][0]["tests"][1]["failures"][0]["expected"] == "1"
