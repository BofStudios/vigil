"""Tool registry and filesystem tool tests."""

from pathlib import Path

import pytest

from vigil.config import Config
from vigil.security import Guard, Risk
from vigil.tools import PermissionDenied, ToolContext, ToolError, build_registry, truncate
from vigil.tools import files as file_tools


@pytest.fixture
def ctx(tmp_path):
    config = Config()
    config.api_key = "test"
    guard = Guard(mode="yolo", confirm=None, audit=False)
    return ToolContext(config=config, guard=guard, provider=None, ui=None, cwd=tmp_path)


# --------------------------------------------------------------- registry
def test_registry_loads_core_groups():
    names = build_registry(Config()).names()
    for expected in ("run_command", "read_file", "write_file", "edit_file", "list_dir"):
        assert expected in names


def test_every_tool_has_a_valid_schema():
    for spec in build_registry(Config()).specs():
        assert spec.name and spec.description
        assert spec.parameters.get("type") == "object"
        assert isinstance(spec.parameters.get("properties", {}), dict)
        assert callable(spec.handler)
        assert isinstance(spec.risk, Risk)


def test_truncate_keeps_head_and_tail():
    result = truncate("a" * 5000 + "END", limit=500)
    assert len(result) < 800
    assert result.startswith("a")
    assert result.endswith("END")


# ------------------------------------------------------------------ files
def test_write_read_edit_roundtrip(ctx, tmp_path):
    target = tmp_path / "note.txt"
    file_tools.write_file(ctx, str(target), "hello\nworld\n")
    assert target.read_text(encoding="utf-8").startswith("hello")

    content = file_tools.read_file(ctx, str(target))
    assert "hello" in content
    assert "1\t" in content  # line numbers

    file_tools.edit_file(ctx, str(target), "world", "vigil")
    assert "vigil" in target.read_text(encoding="utf-8")


def test_edit_requires_a_unique_match(ctx, tmp_path):
    target = tmp_path / "repeated.txt"
    target.write_text("same\nsame\n", encoding="utf-8")
    with pytest.raises(ToolError):
        file_tools.edit_file(ctx, str(target), "same", "different")
    file_tools.edit_file(ctx, str(target), "same", "different", replace_all=True)
    assert "same" not in target.read_text(encoding="utf-8")


def test_edit_with_missing_text_raises(ctx, tmp_path):
    target = tmp_path / "a.txt"
    target.write_text("content", encoding="utf-8")
    with pytest.raises(ToolError):
        file_tools.edit_file(ctx, str(target), "missing", "new")


def test_read_missing_file(ctx, tmp_path):
    with pytest.raises(ToolError):
        file_tools.read_file(ctx, str(tmp_path / "nope.txt"))


def test_search_and_find(ctx, tmp_path):
    (tmp_path / "code.py").write_text("def hello():\n    return 42\n", encoding="utf-8")
    (tmp_path / "data.txt").write_text("other content\n", encoding="utf-8")

    assert "code.py" in file_tools.find_files(ctx, "*.py", str(tmp_path))

    hits = file_tools.search_text(ctx, "hello", str(tmp_path))
    assert "code.py" in hits and "1:" in hits


def test_delete_needs_recursive_for_nonempty(ctx, tmp_path):
    folder = tmp_path / "folder"
    folder.mkdir()
    (folder / "inside.txt").write_text("x", encoding="utf-8")
    with pytest.raises(ToolError):
        file_tools.delete_path(ctx, str(folder))
    file_tools.delete_path(ctx, str(folder), recursive=True)
    assert not folder.exists()


@pytest.mark.parametrize(
    "system_path",
    [
        "C:/Windows/System32/vigil_test.txt",
        r"C:\Windows\System32\vigil_test.txt",
        "/etc/vigil_test.conf",
        "/usr/bin/vigil_test",
    ],
)
def test_write_to_system_path_is_blocked(ctx, system_path):
    """Foreign-style system paths must be blocked on every platform.

    A path like "C:/Windows/..." is relative on Linux (and "/etc/..." is
    drive-less on Windows), so resolving it first would turn it into a harmless
    path under the cwd. The guard checks the raw path too.
    """
    with pytest.raises(PermissionDenied):
        file_tools.write_file(ctx, system_path, "x")


def test_reading_an_ssh_key_is_blocked(ctx, tmp_path):
    key = tmp_path / ".ssh" / "id_rsa"
    key.parent.mkdir(parents=True)
    key.write_text("PRIVATE KEY", encoding="utf-8")
    with pytest.raises(PermissionDenied):
        file_tools.read_file(ctx, str(key))


def test_relative_paths_resolve_against_cwd(ctx, tmp_path):
    file_tools.write_file(ctx, "sub/file.txt", "content")
    assert (tmp_path / "sub" / "file.txt").exists()


# --------------------------------------------------------------- terminal
def test_run_command_executes(ctx):
    from vigil.tools import shell as shell_tools

    output = shell_tools.run_command(ctx, "echo vigil_test")
    assert "vigil_test" in output
    assert "exit code: 0" in output


def test_run_command_blocked_is_denied(ctx):
    from vigil.tools import shell as shell_tools

    with pytest.raises(PermissionDenied):
        shell_tools.run_command(ctx, "vssadmin delete shadows /all")


def test_change_dir(ctx, tmp_path):
    from vigil.tools import shell as shell_tools

    sub = tmp_path / "project"
    sub.mkdir()
    shell_tools.change_dir(ctx, str(sub))
    assert ctx.cwd == Path(sub).resolve()
