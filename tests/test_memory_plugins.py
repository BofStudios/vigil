"""Persistent memory and plugin system tests."""

import pytest

from vigil import memory
from vigil.tools import Registry


@pytest.fixture
def temp_memory(tmp_path, monkeypatch):
    """Redirect memory files into a temporary folder."""
    monkeypatch.setattr(memory, "MEMORY_DIR", tmp_path / "memory")
    monkeypatch.setattr(memory, "GLOBAL_FILE", tmp_path / "memory" / "global.md")
    monkeypatch.setattr(memory, "PROJECT_DIR", tmp_path / "memory" / "projects")
    return tmp_path


# ----------------------------------------------------------------- memory
def test_add_and_search(temp_memory, tmp_path):
    memory.add("The user prefers short answers.", scope="global")
    memory.add("This project is written in Python.", scope="project", cwd=tmp_path)

    assert len(memory.search("", scope="all", cwd=tmp_path)) == 2

    global_only = memory.search("short", scope="global", cwd=tmp_path)
    assert len(global_only) == 1
    assert "short" in global_only[0][1]


def test_duplicate_is_not_added_twice(temp_memory, tmp_path):
    memory.add("Same fact", scope="global")
    assert "Already in memory" in memory.add("Same fact", scope="global")
    assert len(memory.search("", scope="global", cwd=tmp_path)) == 1


def test_empty_fact_is_rejected(temp_memory):
    with pytest.raises(ValueError):
        memory.add("   ", scope="global")


def test_remove(temp_memory, tmp_path):
    memory.add("Note to delete", scope="global")
    memory.add("Note to keep", scope="global")
    assert "1 note(s) deleted" in memory.remove("to delete", scope="global", cwd=tmp_path)
    remaining = memory.search("", scope="global", cwd=tmp_path)
    assert len(remaining) == 1
    assert "keep" in remaining[0][1]


def test_remove_without_a_match(temp_memory, tmp_path):
    assert "No matching note" in memory.remove("missing", scope="all", cwd=tmp_path)


def test_projects_are_isolated(temp_memory, tmp_path):
    project_a = tmp_path / "a"
    project_b = tmp_path / "b"
    project_a.mkdir()
    project_b.mkdir()

    memory.add("belongs to project A", scope="project", cwd=project_a)
    assert len(memory.search("", scope="project", cwd=project_a)) == 1
    assert len(memory.search("", scope="project", cwd=project_b)) == 0


def test_as_prompt_includes_both_scopes(temp_memory, tmp_path):
    memory.add("global fact", scope="global")
    memory.add("project fact", scope="project", cwd=tmp_path)
    prompt = memory.as_prompt(tmp_path)
    assert "global fact" in prompt
    assert "project fact" in prompt


def test_as_prompt_is_empty_without_notes(temp_memory, tmp_path):
    assert memory.as_prompt(tmp_path) == ""


def test_prompt_size_is_capped(temp_memory, tmp_path):
    for index in range(120):
        memory.add("a fairly long note number " + str(index) + " " + ("x" * 80), scope="global")
    assert len(memory.as_prompt(tmp_path)) <= memory.MAX_PROMPT_CHARS


# ---------------------------------------------------------------- plugins
PLUGIN_SOURCE = '''
from vigil.security import Risk
from vigil.tools import ToolSpec


def greet(ctx, name=""):
    return "hello " + name


TOOLS = [
    ToolSpec(
        name="greet",
        description="Says hello.",
        parameters={"type": "object", "properties": {"name": {"type": "string"}}},
        handler=greet,
        risk=Risk.SAFE,
    ),
]
'''


def test_plugin_is_loaded(tmp_path):
    (tmp_path / "greeter.py").write_text(PLUGIN_SOURCE, encoding="utf-8")

    registry = Registry()
    added = registry.load_plugins(tmp_path)

    assert added == 1
    assert "greet" in registry.names()
    assert registry.plugins == ["greeter.py"]
    # tools without an explicit group are tagged as "plugin"
    assert registry.get("greet").group == "plugin"


def test_broken_plugin_is_skipped_without_crashing(tmp_path):
    (tmp_path / "broken.py").write_text("this is not valid python !!!", encoding="utf-8")
    (tmp_path / "working.py").write_text(PLUGIN_SOURCE, encoding="utf-8")

    registry = Registry()
    added = registry.load_plugins(tmp_path)

    assert added == 1
    assert "greet" in registry.names()
    assert "plugin:broken.py" in registry.skipped


def test_plugin_without_tools_is_reported(tmp_path):
    (tmp_path / "empty.py").write_text("VARIABLE = 1\n", encoding="utf-8")
    registry = Registry()
    assert registry.load_plugins(tmp_path) == 0
    assert "TOOLS" in registry.skipped["plugin:empty.py"]


def test_underscore_files_are_ignored(tmp_path):
    (tmp_path / "_helper.py").write_text(PLUGIN_SOURCE, encoding="utf-8")
    registry = Registry()
    assert registry.load_plugins(tmp_path) == 0
    assert registry.names() == []


def test_missing_plugin_dir_is_safe(tmp_path):
    assert Registry().load_plugins(tmp_path / "does_not_exist") == 0
