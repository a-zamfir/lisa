from __future__ import annotations

import json
import os
import subprocess
import sys
import time
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple


class SkillRunner:
    def __init__(self, repo_root: Path) -> None:
        self._repo_root = repo_root

    def _build_env(self) -> Dict[str, str]:
        allowed = [
            "APPDATA",
            "COMSPEC",
            "LOCALAPPDATA",
            "PATH",
            "PATHEXT",
            "SYSTEMDRIVE",
            "SYSTEMROOT",
            "TEMP",
            "TMP",
            "USERPROFILE",
            "WINDIR",
        ]
        env = {key: value for key, value in os.environ.items() if key in allowed}
        env["PYTHONDONTWRITEBYTECODE"] = "1"
        env["PYTHONIOENCODING"] = "utf-8"
        return env

    def _resolve_command(self, command: str, base_dir: Optional[Path] = None) -> Path:
        command_path = Path(command)
        if not command_path.is_absolute():
            if base_dir is not None:
                command_path = (base_dir / command_path).resolve()
            else:
                command_path = (self._repo_root / command_path).resolve()
        if not self._is_within_repo(command_path):
            raise ValueError("Skill command must be inside the repo")
        if not command_path.exists():
            raise FileNotFoundError(f"Skill command not found: {command_path}")
        return command_path

    def _is_within_repo(self, path: Path) -> bool:
        try:
            path.relative_to(self._repo_root)
            return True
        except ValueError:
            return False

    def _resolve_interpreter(self, python_path: str) -> Path:
        interpreter_path = Path(python_path)
        if not interpreter_path.is_absolute():
            interpreter_path = (self._repo_root / interpreter_path).resolve()
        if not self._is_within_repo(interpreter_path):
            raise ValueError("Skill interpreter must be inside the repo")
        if not interpreter_path.exists():
            raise FileNotFoundError(f"Skill interpreter not found: {interpreter_path}")
        return interpreter_path

    def _stringify_arg(self, value: Any) -> str:
        if isinstance(value, bool):
            return "true" if value else "false"
        return str(value)

    def _resolve_env_value(self, value: Any, *, base_dir: Optional[Path] = None) -> Optional[str]:
        if not isinstance(value, str):
            return None
        resolved = value.replace("{repo_root}", str(self._repo_root))
        if base_dir is not None:
            resolved = resolved.replace("{base_dir}", str(base_dir))
        resolved = os.path.expandvars(resolved)
        maybe_path = Path(resolved)
        if not maybe_path.is_absolute() and any(token in value for token in ("/", "\\", ".", "{repo_root}", "{base_dir}")):
            anchor = base_dir or self._repo_root
            resolved = str((anchor / maybe_path).resolve())
        return resolved

    def _apply_static_env(self, tool: Dict[str, Any], env: Dict[str, str], *, base_dir: Optional[Path] = None) -> Dict[str, str]:
        static_env = tool.get("env") or {}
        if not isinstance(static_env, dict):
            return env

        updated = dict(env)
        for key, raw_value in static_env.items():
            if not isinstance(key, str) or not key:
                continue
            resolved = self._resolve_env_value(raw_value, base_dir=base_dir)
            if resolved is None:
                continue
            updated[key] = resolved

        ensure_dirs = tool.get("ensure_dirs") or []
        if isinstance(ensure_dirs, list):
            for env_name in ensure_dirs:
                if not isinstance(env_name, str) or not env_name:
                    continue
                target = updated.get(env_name)
                if not target:
                    continue
                try:
                    Path(target).mkdir(parents=True, exist_ok=True)
                except OSError:
                    continue

        return updated

    def _validate_args(self, schema: Dict[str, Any], args: Dict[str, Any]) -> Dict[str, Any]:
        def _is_json_safe(value: Any) -> bool:
            try:
                json.dumps(value)
                return True
            except TypeError:
                return False

        props = schema.get("properties") if isinstance(schema, dict) else None
        if not isinstance(props, dict):
            return {key: value for key, value in args.items() if _is_json_safe(value)}

        validated: Dict[str, Any] = {}
        for key, value in args.items():
            expected = props.get(key, {})
            expected_type = expected.get("type") if isinstance(expected, dict) else None
            if expected_type is None:
                if _is_json_safe(value):
                    validated[key] = value
                continue
            if expected_type == "string" and isinstance(value, str):
                validated[key] = value
            elif expected_type == "integer" and isinstance(value, int) and not isinstance(value, bool):
                validated[key] = value
            elif expected_type == "number" and isinstance(value, (int, float)) and not isinstance(value, bool):
                validated[key] = value
            elif expected_type == "boolean" and isinstance(value, bool):
                validated[key] = value
            elif expected_type == "array" and isinstance(value, list):
                validated[key] = value
            elif expected_type == "object" and isinstance(value, dict):
                validated[key] = value
            else:
                # Type mismatch - drop the argument rather than failing.
                continue
        return validated

    def _build_native_cli_args(self, tool: Dict[str, Any], validated_args: Dict[str, Any]) -> Tuple[List[str], Dict[str, str]]:
        cli_args = tool.get("cli_args") or []
        if not isinstance(cli_args, list):
            cli_args = []
        built_args = [self._stringify_arg(arg) for arg in cli_args]
        env_updates: Dict[str, str] = {}
        arg_flags = tool.get("arg_flags") or {}
        positional: List[Tuple[int, str]] = []

        for key, value in validated_args.items():
            spec = arg_flags.get(key, {}) if isinstance(arg_flags, dict) else {}
            if not isinstance(spec, dict):
                continue

            env_name = spec.get("env")
            if isinstance(env_name, str) and env_name:
                env_updates[env_name] = self._stringify_arg(value)
                continue

            position = spec.get("position")
            if isinstance(position, int):
                positional.append((position, self._stringify_arg(value)))
                continue

            flag = spec.get("flag")
            if not isinstance(flag, str) or not flag:
                continue

            if isinstance(value, bool):
                if value:
                    built_args.append(flag)
                elif spec.get("emit_false_value"):
                    built_args.extend([flag, self._stringify_arg(value)])
                continue

            if isinstance(value, list):
                if not value:
                    continue
                if spec.get("repeat", True):
                    for item in value:
                        built_args.extend([flag, self._stringify_arg(item)])
                else:
                    separator = str(spec.get("separator") or ",")
                    built_args.extend([flag, separator.join(self._stringify_arg(item) for item in value)])
                continue

            built_args.extend([flag, self._stringify_arg(value)])

        if positional:
            for _, item in sorted(positional, key=lambda pair: pair[0]):
                built_args.append(item)

        return built_args, env_updates

    def _build_native_command(self, command_path: Path, built_args: List[str]) -> List[str]:
        suffix = command_path.suffix.lower()
        if suffix in {".cmd", ".bat"}:
            comspec = os.environ.get("COMSPEC") or os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32", "cmd.exe")
            cmdline = subprocess.list2cmdline([str(command_path), *built_args])
            return [comspec, "/d", "/s", "/c", cmdline]
        return [str(command_path), *built_args]

    def _parse_native_stdout(self, stdout: str, stderr: str) -> Dict[str, Any]:
        text = stdout.strip()
        if not text:
            return {"success": True, "data": {}, "error": None, "meta": {"stderr": stderr.strip() or None}}

        try:
            parsed = json.loads(text)
        except json.JSONDecodeError:
            return {"success": True, "data": {"stdout": text}, "error": None, "meta": {"stderr": stderr.strip() or None}}

        if isinstance(parsed, dict) and ("success" in parsed or "error" in parsed):
            parsed.setdefault("meta", {})
            if stderr.strip() and isinstance(parsed.get("meta"), dict):
                parsed["meta"].setdefault("stderr", stderr.strip())
            return parsed

        return {"success": True, "data": parsed, "error": None, "meta": {"stderr": stderr.strip() or None}}

    def _run_python_tool(
        self,
        tool: Dict[str, Any],
        command_path: Path,
        validated_args: Dict[str, Any],
        *,
        base_dir: Optional[Path] = None,
    ) -> Dict[str, Any]:
        cli_args = tool.get("cli_args") or []
        if not isinstance(cli_args, list):
            cli_args = []

        python_override = tool.get("python")
        if python_override:
            try:
                interpreter_path = self._resolve_interpreter(str(python_override))
                interpreter = str(interpreter_path)
            except (ValueError, FileNotFoundError) as exc:
                return {"isError": True, "error": str(exc)}
        else:
            interpreter = sys.executable
        cmd = [interpreter, str(command_path), *[str(a) for a in cli_args]]

        payload = json.dumps({"args": validated_args})
        timeout_ms = int(tool.get("timeout_ms") or 15000)
        max_output_kb = int(tool.get("max_output_kb") or 128)
        env = self._apply_static_env(tool, self._build_env(), base_dir=base_dir)

        start = time.monotonic()
        try:
            result = subprocess.run(
                cmd,
                input=payload,
                text=True,
                capture_output=True,
                cwd=str(command_path.parent),
                timeout=timeout_ms / 1000.0,
                env=env,
            )
        except subprocess.TimeoutExpired:
            return {"isError": True, "error": f"Skill timed out after {timeout_ms}ms"}
        except Exception as exc:
            return {"isError": True, "error": f"Skill execution failed: {exc}"}

        duration_ms = int((time.monotonic() - start) * 1000)
        stdout = result.stdout or ""
        stderr = result.stderr or ""
        if len(stdout.encode("utf-8")) > max_output_kb * 1024:
            return {"isError": True, "error": "Skill output exceeded size limit"}

        try:
            data = json.loads(stdout) if stdout.strip() else {}
        except json.JSONDecodeError:
            data = {"success": False, "error": "Skill returned non-JSON output", "raw": stdout.strip()}

        if not isinstance(data, dict):
            data = {"success": False, "error": "Skill returned unexpected output", "raw": data}

        if result.returncode != 0 and not data.get("error"):
            data["error"] = stderr.strip() or "Skill failed"

        if data.get("success") is False or data.get("error"):
            return {"isError": True, "error": data.get("error", "Skill failed"), "duration_ms": duration_ms}

        return {"isError": False, "content": [{"type": "text", "text": json.dumps(data)}], "duration_ms": duration_ms}

    def _run_native_cli_tool(
        self,
        tool: Dict[str, Any],
        command_path: Path,
        validated_args: Dict[str, Any],
        *,
        base_dir: Optional[Path] = None,
    ) -> Dict[str, Any]:
        built_args, env_updates = self._build_native_cli_args(tool, validated_args)
        cmd = self._build_native_command(command_path, built_args)
        timeout_ms = int(tool.get("timeout_ms") or 15000)
        max_output_kb = int(tool.get("max_output_kb") or 128)
        env = self._apply_static_env(tool, self._build_env(), base_dir=base_dir)
        env.update(env_updates)

        start = time.monotonic()
        try:
            result = subprocess.run(
                cmd,
                text=True,
                capture_output=True,
                cwd=str(command_path.parent),
                timeout=timeout_ms / 1000.0,
                env=env,
            )
        except subprocess.TimeoutExpired:
            return {"isError": True, "error": f"Skill timed out after {timeout_ms}ms"}
        except Exception as exc:
            return {"isError": True, "error": f"Skill execution failed: {exc}"}

        duration_ms = int((time.monotonic() - start) * 1000)
        stdout = result.stdout or ""
        stderr = result.stderr or ""
        if len(stdout.encode("utf-8")) > max_output_kb * 1024:
            return {"isError": True, "error": "Skill output exceeded size limit"}

        data = self._parse_native_stdout(stdout, stderr)
        if result.returncode != 0:
            error = data.get("error") if isinstance(data, dict) else None
            if not error:
                error = stderr.strip() or stdout.strip() or "Skill failed"
            return {"isError": True, "error": error, "duration_ms": duration_ms}

        if data.get("success") is False or data.get("error"):
            return {"isError": True, "error": data.get("error", "Skill failed"), "duration_ms": duration_ms}

        return {"isError": False, "content": [{"type": "text", "text": json.dumps(data)}], "duration_ms": duration_ms}

    def run(self, tool: Dict[str, Any], args: Dict[str, Any]) -> Dict[str, Any]:
        command = tool.get("command")
        if not command:
            return {"isError": True, "error": "Missing command for skill"}

        base_dir = Path(tool["base_dir"]) if tool.get("base_dir") else None
        try:
            command_path = self._resolve_command(command, base_dir)
        except (ValueError, FileNotFoundError) as exc:
            return {"isError": True, "error": str(exc)}
        schema = tool.get("args_schema") or {}
        validated_args = self._validate_args(schema, args)
        execution_mode = str(tool.get("execution_mode") or "python").strip().lower()

        if execution_mode == "python":
            if command_path.suffix.lower() != ".py":
                return {"isError": True, "error": f"Python execution mode requires a .py command: {command_path.name}"}
            return self._run_python_tool(tool, command_path, validated_args, base_dir=base_dir)

        if execution_mode == "native-cli":
            return self._run_native_cli_tool(tool, command_path, validated_args, base_dir=base_dir)

        return {"isError": True, "error": f"Unsupported skill execution mode: {execution_mode}"}


class SkillsClient:
    def __init__(self) -> None:
        self._repo_root = Path(__file__).resolve().parents[3]
        self._skills_root = self._repo_root / "skills"
        self._skills: List[Dict[str, Any]] = []
        self._skill_map: Dict[str, Dict[str, Any]] = {}
        self._tools: List[Dict[str, Any]] = []
        self._tool_map: Dict[str, Dict[str, Any]] = {}
        self._initialized = False
        self._runner = SkillRunner(self._repo_root)

    @property
    def available(self) -> bool:
        if not self._skills_root.exists():
            return False
        return any(self._skills_root.glob("*/SKILL.md"))

    async def initialize(self) -> None:
        if not self.available:
            return
        self._load_registry()
        self._initialized = True

    def _load_registry(self) -> None:
        self._skills.clear()
        self._skill_map.clear()
        self._tools.clear()
        self._tool_map.clear()
        if not self._skills_root.exists():
            return

        for skill_file in sorted(self._skills_root.glob("*/SKILL.md")):
            skill_dir = skill_file.parent
            document = self._parse_skill_document(skill_file)
            frontmatter = document["frontmatter"]
            skill_name = frontmatter.get("name") if frontmatter else None
            if not skill_name or skill_name != skill_dir.name:
                continue
            description = frontmatter.get("description", "") if frontmatter else ""
            resources = self._list_skill_resources(skill_dir)
            skill = {
                "name": skill_name,
                "description": description,
                "path": str(skill_dir),
                "skill_file": str(skill_file),
                "instructions": document["body"],
                "resources": resources,
            }
            self._skills.append(skill)
            self._skill_map[skill_name] = skill

            tools_manifest = skill_dir / "tools.json"
            if not tools_manifest.exists():
                continue

            try:
                manifest = json.loads(tools_manifest.read_text(encoding="utf-8"))
            except json.JSONDecodeError:
                continue

            defaults = manifest.get("defaults", {}) if isinstance(manifest, dict) else {}
            tools = manifest.get("tools", []) if isinstance(manifest, dict) else []
            for tool in tools:
                if not isinstance(tool, dict):
                    continue
                merged_tool = self._merge_tool_defaults(defaults, tool)
                tool_id = merged_tool.get("id")
                if not tool_id:
                    continue
                item = {
                    "name": tool_id,
                    "description": merged_tool.get("description", ""),
                    "friendly_desc": merged_tool.get("friendly_desc", f"running {tool_id}"),
                    "approval_required": bool(merged_tool.get("requires_approval", False)),
                    "category": skill_name,
                    "execution_mode": merged_tool.get("execution_mode", "python"),
                    "command": merged_tool.get("command"),
                    "cli_args": merged_tool.get("cli_args", []),
                    "arg_flags": merged_tool.get("arg_flags", {}),
                    "env": merged_tool.get("env", {}),
                    "ensure_dirs": merged_tool.get("ensure_dirs", []),
                    "args_schema": merged_tool.get("args_schema", {}),
                    "timeout_ms": merged_tool.get("timeout_ms", 15000),
                    "max_output_kb": merged_tool.get("max_output_kb", 128),
                    "read_only": bool(merged_tool.get("read_only", False)),
                    "base_dir": str(skill_dir),
                }
                self._tools.append(item)
                self._tool_map[tool_id] = item

    def _merge_tool_defaults(self, defaults: Any, tool: Dict[str, Any]) -> Dict[str, Any]:
        if not isinstance(defaults, dict):
            return dict(tool)

        merged: Dict[str, Any] = dict(defaults)
        for key, value in tool.items():
            if key in {"env", "arg_flags"}:
                base = merged.get(key, {})
                if isinstance(base, dict) and isinstance(value, dict):
                    merged[key] = {**base, **value}
                else:
                    merged[key] = value
                continue

            if key == "ensure_dirs":
                base_list = merged.get(key, [])
                if isinstance(base_list, list) and isinstance(value, list):
                    deduped: List[Any] = []
                    for item in [*base_list, *value]:
                        if item not in deduped:
                            deduped.append(item)
                    merged[key] = deduped
                else:
                    merged[key] = value
                continue

            if key == "args_schema":
                base_schema = merged.get(key, {})
                if isinstance(base_schema, dict) and isinstance(value, dict):
                    schema = dict(base_schema)
                    base_props = schema.get("properties", {})
                    tool_props = value.get("properties", {})
                    if isinstance(base_props, dict) and isinstance(tool_props, dict):
                        schema["properties"] = {**base_props, **tool_props}
                    for schema_key, schema_value in value.items():
                        if schema_key == "properties":
                            continue
                        if schema_key == "required" and isinstance(schema.get("required"), list) and isinstance(schema_value, list):
                            required: List[Any] = []
                            for item in [*schema.get("required", []), *schema_value]:
                                if item not in required:
                                    required.append(item)
                            schema["required"] = required
                        else:
                            schema[schema_key] = schema_value
                    merged[key] = schema
                else:
                    merged[key] = value
                continue

            merged[key] = value
        return merged

    def _parse_skill_document(self, path: Path) -> Dict[str, Any]:
        text = path.read_text(encoding="utf-8")
        lines = text.splitlines()
        if not lines or lines[0].strip() != "---":
            return {"frontmatter": {}, "body": text.strip()}

        fm_lines = []
        body_start = 0
        for idx, line in enumerate(lines[1:], start=1):
            if line.strip() == "---":
                body_start = idx + 1
                break
            fm_lines.append(line)

        front: Dict[str, str] = {}
        for line in fm_lines:
            raw = line.strip()
            if not raw or raw.startswith("#") or ":" not in raw:
                continue
            key, value = raw.split(":", 1)
            key = key.strip()
            value = value.strip()
            if value.startswith(("'", "\"")) and value.endswith(("'", "\"")) and len(value) >= 2:
                value = value[1:-1]
            front[key] = value
        body = "\n".join(lines[body_start:]).strip()
        return {"frontmatter": front, "body": body}

    def _list_skill_resources(self, skill_dir: Path) -> List[str]:
        resources: List[str] = []
        for folder_name in ("scripts", "references", "assets"):
            folder = skill_dir / folder_name
            if not folder.exists():
                continue
            for path in sorted(folder.rglob("*")):
                if path.is_file():
                    resources.append(str(path.relative_to(skill_dir)).replace("\\", "/"))
        return resources

    def _build_activate_skill_tool(self, skills: List[Dict[str, Any]]) -> Optional[Dict[str, Any]]:
        if not skills:
            return None
        return {
            "name": "activate_skill",
            "description": "Load the full instructions for a skill when the current task matches its description.",
            "category": "skills",
            "read_only": True,
            "approval_required": False,
            "args_schema": {
                "type": "object",
                "properties": {
                    "skill_name": {
                        "type": "string",
                        "enum": [skill["name"] for skill in skills],
                    }
                },
                "required": ["skill_name"],
                "additionalProperties": False,
            },
        }

    def _build_payload(self, skills: List[Dict[str, Any]], tools: List[Dict[str, Any]]) -> Dict[str, Any]:
        payload_tools = list(tools)
        activation_tool = self._build_activate_skill_tool(skills)
        if activation_tool is not None:
            payload_tools = [activation_tool, *payload_tools]
        return {"skills": skills, "tools": payload_tools, "read_only": True}

    async def list_tools(self, force_refresh: bool = False) -> Dict[str, Any]:
        if force_refresh or not self._initialized:
            self._load_registry()
            self._initialized = True
        return self._build_payload(self._skills, self._tools)

    async def list_tool_docs(self, force_refresh: bool = False) -> Dict[str, Any]:
        if force_refresh or not self._initialized:
            self._load_registry()
            self._initialized = True
        return {"skills": self._skills}

    def get_cached_tools(self, active_categories: Optional[List[str]] = None) -> Optional[Dict[str, Any]]:
        if not self._initialized:
            return None
        if active_categories is None:
            return self._build_payload(self._skills, self._tools)
        filtered = [tool for tool in self._tools if tool.get("category") in active_categories]
        filtered_skills = [skill for skill in self._skills if skill.get("name") in active_categories]
        return self._build_payload(filtered_skills, filtered)

    def get_tool_meta(self, name: str) -> Optional[Dict[str, Any]]:
        return self._tool_map.get(name)

    async def call_tool(self, tool: str, args: Optional[Dict[str, Any]] = None) -> Dict[str, Any]:
        if not self._initialized:
            self._load_registry()
            self._initialized = True
        if tool == "activate_skill":
            skill_name = (args or {}).get("skill_name", "")
            skill = self._skill_map.get(skill_name)
            if skill is None:
                return {"isError": True, "error": f"Skill not found: {skill_name}"}
            lines = [
                f"Activated skill: {skill['name']}",
                f"Path: {Path(skill['skill_file']).relative_to(self._repo_root).as_posix()}",
                "",
                skill["instructions"],
            ]
            resources = skill.get("resources") or []
            if resources:
                lines.extend(["", "Bundled resources:"])
                lines.extend([f"- {resource}" for resource in resources])
            return {
                "isError": False,
                "content": [{"type": "text", "text": "\n".join(lines).strip()}],
                "duration_ms": 0,
            }
        meta = self._tool_map.get(tool)
        if not meta:
            return {"error": f"Skill not found: {tool}", "isError": True}
        return self._runner.run(meta, args or {})

    async def warm_client(self) -> None:
        return
