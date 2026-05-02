from __future__ import annotations

import asyncio
import json
from pathlib import Path

from agent_worker.services.skills_client import SkillRunner, SkillsClient


def test_skill_runner_sanitizes_environment(monkeypatch, workspace_temp_dir: Path):
    tmp_path = workspace_temp_dir
    monkeypatch.setenv("PATH", "C:\\Windows\\System32")
    monkeypatch.setenv("TEMP", str(tmp_path))
    monkeypatch.setenv("SECRET_TOKEN", "should-not-leak")

    runner = SkillRunner(tmp_path)
    env = runner._build_env()

    assert env["PATH"] == "C:\\Windows\\System32"
    assert env["TEMP"] == str(tmp_path)
    assert env["PYTHONDONTWRITEBYTECODE"] == "1"
    assert env["PYTHONIOENCODING"] == "utf-8"
    assert "SECRET_TOKEN" not in env


def test_skill_runner_rejects_non_python_entrypoints(workspace_temp_dir: Path):
    tmp_path = workspace_temp_dir
    tool_dir = tmp_path / "skills" / "demo"
    tool_dir.mkdir(parents=True)
    command = tool_dir / "tool.exe"
    command.write_text("", encoding="utf-8")

    runner = SkillRunner(tmp_path)
    result = runner.run(
        {
            "command": str(command),
            "base_dir": str(tool_dir),
            "args_schema": {},
        },
        {},
    )

    assert result["isError"] is True
    assert "requires a .py command" in result["error"]


def test_skill_runner_executes_native_cli_cmd(workspace_temp_dir: Path):
    tmp_path = workspace_temp_dir
    tool_dir = tmp_path / "skills" / "demo"
    tool_dir.mkdir(parents=True)
    command = tool_dir / "echo.cmd"
    command.write_text(
        (
            "@echo off\n"
            "echo {\"success\":true,\"data\":{\"args\":\"%*\"},\"error\":null,\"meta\":{}}\n"
        ),
        encoding="utf-8",
    )

    runner = SkillRunner(tmp_path)
    result = runner.run(
        {
            "execution_mode": "native-cli",
            "command": str(command),
            "base_dir": str(tool_dir),
            "cli_args": ["open"],
            "arg_flags": {
                "session": {"flag": "--session"},
                "url": {"position": 0},
            },
            "args_schema": {
                "properties": {
                    "session": {"type": "string"},
                    "url": {"type": "string"},
                }
            },
        },
        {"session": "demo", "url": "https://example.com"},
    )

    assert result["isError"] is False
    content = json.loads(result["content"][0]["text"])
    assert "--session demo https://example.com" in content["data"]["args"]


def test_skill_runner_applies_static_env_and_ensures_dirs(monkeypatch, workspace_temp_dir: Path):
    tmp_path = workspace_temp_dir
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path / "localappdata"))
    tool_dir = tmp_path / "skills" / "demo"
    tool_dir.mkdir(parents=True)
    command = tool_dir / "echo-env.cmd"
    command.write_text(
        (
            "@echo off\n"
            "echo {\"success\":true,\"data\":{\"socket\":\"%AGENT_BROWSER_SOCKET_DIR%\",\"profile\":\"%AGENT_BROWSER_PROFILE%\"},\"error\":null,\"meta\":{}}\n"
        ),
        encoding="utf-8",
    )

    runner = SkillRunner(tmp_path)
    result = runner.run(
        {
            "execution_mode": "native-cli",
            "command": str(command),
            "base_dir": str(tool_dir),
            "env": {
                "AGENT_BROWSER_SOCKET_DIR": "%LOCALAPPDATA%\\LISA\\skills\\agent-browser\\socket",
                "AGENT_BROWSER_PROFILE": "%LOCALAPPDATA%\\LISA\\skills\\agent-browser\\profile",
            },
            "ensure_dirs": [
                "AGENT_BROWSER_SOCKET_DIR",
                "AGENT_BROWSER_PROFILE",
            ],
            "args_schema": {"properties": {}},
        },
        {},
    )

    assert result["isError"] is False
    socket_dir = tmp_path / "localappdata" / "LISA" / "skills" / "agent-browser" / "socket"
    profile_dir = tmp_path / "localappdata" / "LISA" / "skills" / "agent-browser" / "profile"
    assert socket_dir.exists()
    assert profile_dir.exists()
    assert socket_dir.is_dir()
    assert profile_dir.is_dir()


def test_skills_client_loads_and_filters_registry(workspace_temp_dir: Path):
    tmp_path = workspace_temp_dir
    skills_root = tmp_path / "skills" / "demo"
    scripts_dir = skills_root / "scripts"
    scripts_dir.mkdir(parents=True)

    (skills_root / "SKILL.md").write_text(
        "---\nname: demo\ndescription: demo skill\n---\n# Demo\n",
        encoding="utf-8",
    )
    (skills_root / "tools.json").write_text(
        json.dumps(
            {
                "tools": [
                    {
                        "id": "demo_tool",
                        "description": "demo tool",
                        "command": "scripts/run.py",
                        "args_schema": {"properties": {"query": {"type": "string"}}},
                        "requires_approval": False,
                    }
                ]
            }
        ),
        encoding="utf-8",
    )
    (scripts_dir / "run.py").write_text(
        "import json, sys; json.dump({'success': True, 'data': {'ok': True}}, sys.stdout)",
        encoding="utf-8",
    )

    client = SkillsClient()
    client._repo_root = tmp_path
    client._skills_root = tmp_path / "skills"
    client._load_registry()
    client._initialized = True

    cached = client.get_cached_tools(active_categories=["demo"])

    assert cached is not None
    assert len(cached["skills"]) == 1
    assert len(cached["tools"]) == 2
    assert cached["tools"][0]["name"] == "activate_skill"
    assert cached["tools"][1]["name"] == "demo_tool"
    assert cached["tools"][1]["category"] == "demo"


def test_skills_client_applies_manifest_defaults(workspace_temp_dir: Path):
    tmp_path = workspace_temp_dir
    skills_root = tmp_path / "skills" / "demo"
    scripts_dir = skills_root / "scripts"
    scripts_dir.mkdir(parents=True)

    (skills_root / "SKILL.md").write_text(
        "---\nname: demo\ndescription: demo skill\n---\n# Demo\n",
        encoding="utf-8",
    )
    (skills_root / "tools.json").write_text(
        json.dumps(
            {
                "defaults": {
                    "execution_mode": "native-cli",
                    "command": "scripts/tool.cmd",
                    "env": {"DEMO_HOME": "%LOCALAPPDATA%\\Demo"},
                    "ensure_dirs": ["DEMO_HOME"],
                    "timeout_ms": 1234,
                },
                "tools": [
                    {
                        "id": "demo_tool",
                        "description": "demo tool",
                        "cli_args": ["open"],
                        "args_schema": {
                            "type": "object",
                            "properties": {"query": {"type": "string"}},
                            "required": ["query"],
                        },
                    }
                ],
            }
        ),
        encoding="utf-8",
    )
    (scripts_dir / "tool.cmd").write_text("@echo off\n", encoding="utf-8")

    client = SkillsClient()
    client._repo_root = tmp_path
    client._skills_root = tmp_path / "skills"
    client._load_registry()
    client._initialized = True

    meta = client.get_tool_meta("demo_tool")

    assert meta is not None
    assert meta["execution_mode"] == "native-cli"
    assert meta["command"] == "scripts/tool.cmd"
    assert meta["env"]["DEMO_HOME"].endswith("%LOCALAPPDATA%\\Demo")
    assert meta["ensure_dirs"] == ["DEMO_HOME"]
    assert meta["timeout_ms"] == 1234
    assert meta["cli_args"] == ["open"]
    assert meta["args_schema"]["required"] == ["query"]


def test_skill_runner_executes_python_tool(workspace_temp_dir: Path):
    tmp_path = workspace_temp_dir
    tool_dir = tmp_path / "skills" / "demo" / "scripts"
    tool_dir.mkdir(parents=True)
    command = tool_dir / "run.py"
    command.write_text(
        (
            "import json, sys\n"
            "payload = json.load(sys.stdin)\n"
            "json.dump({'success': True, 'data': payload['args']}, sys.stdout)\n"
        ),
        encoding="utf-8",
    )

    runner = SkillRunner(tmp_path)
    result = runner.run(
        {
            "command": str(command),
            "base_dir": str(tool_dir.parent),
            "args_schema": {"properties": {"query": {"type": "string"}, "count": {"type": "integer"}}},
        },
        {"query": "hello", "count": 2, "drop_me": object()},
    )

    assert result["isError"] is False
    content = json.loads(result["content"][0]["text"])
    assert content["data"] == {"query": "hello", "count": 2}


def test_skill_runner_blocks_repo_escape(workspace_temp_dir: Path):
    runner = SkillRunner(workspace_temp_dir)
    outside = Path(__file__).resolve()
    result = runner.run({"command": str(outside), "base_dir": str(workspace_temp_dir), "args_schema": {}}, {})

    assert result["isError"] is True
    assert "inside the repo" in result["error"]


def test_skill_runner_rejects_malformed_output(workspace_temp_dir: Path):
    tmp_path = workspace_temp_dir
    tool_dir = tmp_path / "skills" / "demo" / "scripts"
    tool_dir.mkdir(parents=True)
    command = tool_dir / "run.py"
    command.write_text("print('not-json')\n", encoding="utf-8")

    runner = SkillRunner(tmp_path)
    result = runner.run(
        {
            "command": str(command),
            "base_dir": str(tool_dir.parent),
            "args_schema": {"properties": {}},
        },
        {},
    )

    assert result["isError"] is True
    assert "non-JSON" in result["error"]


def test_skill_runner_times_out(workspace_temp_dir: Path):
    tmp_path = workspace_temp_dir
    tool_dir = tmp_path / "skills" / "demo" / "scripts"
    tool_dir.mkdir(parents=True)
    command = tool_dir / "run.py"
    command.write_text(
        "import time\n"
        "time.sleep(0.2)\n"
        "print('{\"success\": true, \"data\": {}}')\n",
        encoding="utf-8",
    )

    runner = SkillRunner(tmp_path)
    result = runner.run(
        {
            "command": str(command),
            "base_dir": str(tool_dir.parent),
            "args_schema": {"properties": {}},
            "timeout_ms": 50,
        },
        {},
    )

    assert result["isError"] is True
    assert "timed out" in result["error"]


def test_repo_skill_manifests_expose_expected_read_write_metadata():
    client = SkillsClient()
    client._load_registry()
    client._initialized = True

    create_event = client.get_tool_meta("outlook_create_calendar_event")
    get_events = client.get_tool_meta("outlook_get_calendar_events")
    system_overview = client.get_tool_meta("system_overview")
    browser_open = client.get_tool_meta("agent_browser_open")
    browser_snapshot = client.get_tool_meta("agent_browser_snapshot")

    assert create_event is not None
    assert create_event["category"] == "outlook"
    assert create_event["approval_required"] is True
    assert create_event["read_only"] is False

    assert get_events is not None
    assert get_events["category"] == "outlook"
    assert get_events["approval_required"] is False
    assert get_events["read_only"] is True

    assert system_overview is not None
    assert system_overview["category"] == "windows-os"
    assert system_overview["approval_required"] is True
    assert system_overview["read_only"] is True

    assert browser_open is not None
    assert browser_open["category"] == "agent-browser"
    assert browser_open["approval_required"] is True
    assert browser_open["read_only"] is True
    assert browser_open["execution_mode"] == "native-cli"
    assert browser_open["command"] == "node_modules/.bin/agent-browser.cmd"
    assert browser_open["env"]["AGENT_BROWSER_SOCKET_DIR"].endswith("LISA\\skills\\agent-browser\\socket")
    assert "AGENT_BROWSER_PROFILE" in browser_open["ensure_dirs"]

    assert browser_snapshot is not None
    assert browser_snapshot["category"] == "agent-browser"
    assert browser_snapshot["approval_required"] is False
    assert browser_snapshot["read_only"] is True
    assert browser_snapshot["execution_mode"] == "native-cli"


def test_activate_skill_returns_body_without_frontmatter(workspace_temp_dir: Path):
    tmp_path = workspace_temp_dir
    skills_root = tmp_path / "skills" / "demo"
    scripts_dir = skills_root / "scripts"
    scripts_dir.mkdir(parents=True)

    (skills_root / "SKILL.md").write_text(
        (
            "---\n"
            "name: demo\n"
            "description: demo skill\n"
            "---\n\n"
            "# Demo\n\n"
            "Use this skill to do the demo workflow.\n"
        ),
        encoding="utf-8",
    )
    (skills_root / "tools.json").write_text(json.dumps({"tools": []}), encoding="utf-8")
    (scripts_dir / "run.py").write_text("print('ok')\n", encoding="utf-8")

    client = SkillsClient()
    client._repo_root = tmp_path
    client._skills_root = tmp_path / "skills"
    asyncio.run(client.initialize())

    result = asyncio.run(client.call_tool("activate_skill", {"skill_name": "demo"}))

    assert result["isError"] is False
    text = result["content"][0]["text"]
    assert "Activated skill: demo" in text
    assert "# Demo" in text
    assert "description: demo skill" not in text
    assert "scripts/run.py" in text
