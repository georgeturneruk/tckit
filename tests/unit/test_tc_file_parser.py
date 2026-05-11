"""Unit tests for tckit.utils.tc_file_parser helpers."""

from pathlib import Path
from textwrap import dedent

from tckit.utils.tc_file_parser import (
    parse_plcproj,
    parse_tctto,
    parse_tsproj,
    strip_method_locals,
)


# ---------------------------------------------------------------------------
# strip_method_locals — what gets stripped
# ---------------------------------------------------------------------------


def test_strips_bare_var_block() -> None:
    decl = (
        "METHOD M : BOOL\n"
        "VAR_INPUT\n"
        "    x : INT;\n"
        "END_VAR\n"
        "VAR\n"
        "    y : INT;\n"
        "END_VAR\n"
    )
    result = strip_method_locals(decl)
    assert "VAR_INPUT" in result
    assert "x : INT" in result
    assert "y : INT" not in result
    # The bare VAR opener line is gone too — not just the contents
    assert "\nVAR\n" not in result


def test_strips_var_temp_block() -> None:
    decl = (
        "METHOD M : BOOL\n"
        "VAR_TEMP\n"
        "    nTemp : INT;\n"
        "END_VAR\n"
    )
    result = strip_method_locals(decl)
    assert "VAR_TEMP" not in result
    assert "nTemp" not in result


def test_strips_var_constant_block() -> None:
    decl = (
        "METHOD M : BOOL\n"
        "VAR CONSTANT\n"
        "    nMax : INT := 100;\n"
        "END_VAR\n"
    )
    result = strip_method_locals(decl)
    assert "VAR CONSTANT" not in result
    assert "nMax" not in result


def test_strips_multiple_blocks_in_one_pass() -> None:
    decl = (
        "METHOD M : BOOL\n"
        "VAR_INPUT\n"
        "    x : INT;\n"
        "END_VAR\n"
        "VAR\n"
        "    a : INT;\n"
        "END_VAR\n"
        "VAR_TEMP\n"
        "    b : INT;\n"
        "END_VAR\n"
        "VAR CONSTANT\n"
        "    c : INT := 1;\n"
        "END_VAR\n"
    )
    result = strip_method_locals(decl)
    assert "VAR_INPUT" in result
    assert "x : INT" in result
    for leaked in ("a : INT", "b : INT", "c : INT", "VAR_TEMP", "VAR CONSTANT"):
        assert leaked not in result


# ---------------------------------------------------------------------------
# strip_method_locals — what is preserved (the API surface)
# ---------------------------------------------------------------------------


def test_preserves_var_input() -> None:
    decl = (
        "METHOD M : BOOL\n"
        "VAR_INPUT\n"
        "    x : INT;\n"
        "END_VAR\n"
    )
    assert strip_method_locals(decl) == decl.rstrip()


def test_preserves_var_output() -> None:
    decl = (
        "METHOD M : BOOL\n"
        "VAR_OUTPUT\n"
        "    y : INT;\n"
        "END_VAR\n"
    )
    assert "VAR_OUTPUT" in strip_method_locals(decl)
    assert "y : INT" in strip_method_locals(decl)


def test_preserves_var_in_out() -> None:
    decl = (
        "METHOD M : BOOL\n"
        "VAR_IN_OUT\n"
        "    z : INT;\n"
        "END_VAR\n"
    )
    assert "VAR_IN_OUT" in strip_method_locals(decl)
    assert "z : INT" in strip_method_locals(decl)


def test_preserves_var_inst() -> None:
    # VAR_INST is per-instance state — observable across calls, treat as API.
    decl = (
        "METHOD M : BOOL\n"
        "VAR_INST\n"
        "    nCallCount : UDINT;\n"
        "END_VAR\n"
    )
    assert "VAR_INST" in strip_method_locals(decl)
    assert "nCallCount" in strip_method_locals(decl)


def test_preserves_doc_comments() -> None:
    decl = (
        "// :Description: Does the thing.\n"
        "// :param x: input value\n"
        "METHOD M : BOOL\n"
        "VAR_INPUT\n"
        "    x : INT; // x docstring\n"
        "END_VAR\n"
        "VAR\n"
        "    n : INT;\n"
        "END_VAR\n"
    )
    result = strip_method_locals(decl)
    assert "// :Description: Does the thing." in result
    assert "// :param x: input value" in result
    assert "// x docstring" in result


# ---------------------------------------------------------------------------
# Edge cases
# ---------------------------------------------------------------------------


def test_no_var_blocks_at_all_is_unchanged() -> None:
    decl = "METHOD M : BOOL\n"
    assert strip_method_locals(decl) == "METHOD M : BOOL"


def test_only_var_input_returns_input_intact() -> None:
    decl = "METHOD M : BOOL\nVAR_INPUT\n    x : INT;\nEND_VAR\n"
    result = strip_method_locals(decl)
    assert "VAR_INPUT" in result
    assert "END_VAR" in result


def test_case_insensitive_var_keyword() -> None:
    decl = (
        "METHOD M : BOOL\n"
        "var\n"
        "    n : INT;\n"
        "end_var\n"
    )
    result = strip_method_locals(decl)
    assert "n : INT" not in result


def test_does_not_swallow_var_input_following_var() -> None:
    # Adversarial: a stripped VAR block must not eat the next VAR_INPUT.
    decl = (
        "METHOD M : BOOL\n"
        "VAR\n"
        "    n : INT;\n"
        "END_VAR\n"
        "VAR_INPUT\n"
        "    x : INT;\n"
        "END_VAR\n"
    )
    result = strip_method_locals(decl)
    assert "n : INT" not in result
    assert "VAR_INPUT" in result
    assert "x : INT" in result


def test_indented_var_block_is_stripped() -> None:
    # Some teams indent VAR blocks. The opener is still its own token on a line.
    decl = (
        "METHOD M : BOOL\n"
        "    VAR\n"
        "        n : INT;\n"
        "    END_VAR\n"
    )
    result = strip_method_locals(decl)
    assert "n : INT" not in result


# ---------------------------------------------------------------------------
# parse_plcproj — library references with MSBuild namespace
# ---------------------------------------------------------------------------


PLCPROJ_XML = dedent("""\
    <?xml version="1.0" encoding="utf-8"?>
    <Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
      <ItemGroup>
        <PlaceholderReference Include="Tc2_Standard">
          <DefaultResolution>Tc2_Standard, * (Beckhoff Automation GmbH)</DefaultResolution>
        </PlaceholderReference>
        <PlaceholderReference Include="Tc2_System">
          <DefaultResolution>Tc2_System, 3.4.20.0 (Beckhoff Automation GmbH)</DefaultResolution>
        </PlaceholderReference>
      </ItemGroup>
      <ItemGroup>
        <LibraryReference Include="Base Interfaces,newest,System">
          <Namespace>IBaseLibrary</Namespace>
        </LibraryReference>
      </ItemGroup>
    </Project>
    """)


def test_parse_plcproj_placeholder_with_wildcard_version(tmp_path: Path) -> None:
    plcproj = tmp_path / "p.plcproj"
    plcproj.write_text(PLCPROJ_XML, encoding="utf-8")
    libs = parse_plcproj(plcproj)["libraries"]
    tc2_std = next(lib for lib in libs if lib["name"] == "Tc2_Standard")
    assert tc2_std["version"] == "*"
    assert tc2_std["placeholder"] == "Tc2_Standard"


def test_parse_plcproj_placeholder_with_pinned_version(tmp_path: Path) -> None:
    plcproj = tmp_path / "p.plcproj"
    plcproj.write_text(PLCPROJ_XML, encoding="utf-8")
    libs = parse_plcproj(plcproj)["libraries"]
    tc2_sys = next(lib for lib in libs if lib["name"] == "Tc2_System")
    assert tc2_sys["version"] == "3.4.20.0"


def test_parse_plcproj_direct_library_reference(tmp_path: Path) -> None:
    plcproj = tmp_path / "p.plcproj"
    plcproj.write_text(PLCPROJ_XML, encoding="utf-8")
    libs = parse_plcproj(plcproj)["libraries"]
    base = next(lib for lib in libs if lib["name"] == "Base Interfaces")
    assert base["version"] == "newest"
    assert base["placeholder"] is None


# ---------------------------------------------------------------------------
# parse_tsproj — 100ns ticks converted to microseconds
# ---------------------------------------------------------------------------


TSPROJ_XML = dedent("""\
    <?xml version="1.0" encoding="UTF-8"?>
    <TcSmProject TcSmVersion="1.0">
      <Project>
        <System>
          <Tasks>
            <Task Id="3" Priority="20" CycleTime="100000">
              <Name>PlcTask</Name>
            </Task>
          </Tasks>
        </System>
      </Project>
    </TcSmProject>
    """)


def test_parse_tsproj_converts_100ns_to_microseconds(tmp_path: Path) -> None:
    tsproj = tmp_path / "p.tsproj"
    tsproj.write_text(TSPROJ_XML, encoding="utf-8")
    tasks = parse_tsproj(tsproj)["tasks"]
    assert len(tasks) == 1
    assert tasks[0]["name"] == "PlcTask"
    assert tasks[0]["cycle_time_us"] == 10000
    assert tasks[0]["priority"] == 20
    assert tasks[0]["programs"] == []


# ---------------------------------------------------------------------------
# parse_tctto — authoritative task source
# ---------------------------------------------------------------------------


TCTTO_XML = dedent("""\
    <?xml version="1.0" encoding="utf-8"?>
    <TcPlcObject Version="1.1.0.1">
      <Task Name="PlcTask" Id="{00000000-0000-0000-0000-000000000010}">
        <CycleTime>10000</CycleTime>
        <Priority>20</Priority>
        <PouCall>
          <Name>PRG_MAIN</Name>
        </PouCall>
      </Task>
    </TcPlcObject>
    """)


def test_parse_tctto_returns_microsecond_cycle(tmp_path: Path) -> None:
    tctto = tmp_path / "PlcTask.TcTTO"
    tctto.write_text(TCTTO_XML, encoding="utf-8")
    data = parse_tctto(tctto)
    assert data["name"] == "PlcTask"
    assert data["cycle_time_us"] == 10000
    assert data["priority"] == 20
    assert data["programs"] == ["PRG_MAIN"]
