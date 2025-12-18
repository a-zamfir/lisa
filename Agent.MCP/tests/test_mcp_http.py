from fastapi.testclient import TestClient

import importlib.util
from pathlib import Path


def _load_app():
    path = Path(__file__).resolve().parents[1] / "main.py"
    spec = importlib.util.spec_from_file_location("agent_mcp_main", path)
    module = importlib.util.module_from_spec(spec)
    assert spec and spec.loader
    spec.loader.exec_module(module)
    return module.app


def test_tools_minimal():
    app = _load_app()
    client = TestClient(app)
    resp = client.get("/tools")
    assert resp.status_code == 200
    data = resp.json()
    assert "tools" in data
    assert all("name" in t and "description" in t for t in data["tools"])


def test_call_unknown_tool():
    app = _load_app()
    client = TestClient(app)
    resp = client.post("/call", json={"tool": "does_not_exist", "args": {}})
    assert resp.status_code == 200
    data = resp.json()
    assert "error" in data
