import os

from fastapi.testclient import TestClient

from agent_mcp.app import create_app


def test_tools_minimal():
    os.environ["MCP_AUTH_TOKEN"] = "test-token"
    app = create_app()
    client = TestClient(app)
    resp = client.get("/tools", headers={"X-Mcp-Token": "test-token"})
    assert resp.status_code == 200
    data = resp.json()
    assert "tools" in data
    assert all("name" in t and "description" in t for t in data["tools"])


def test_call_unknown_tool():
    os.environ["MCP_AUTH_TOKEN"] = "test-token"
    app = create_app()
    client = TestClient(app)
    resp = client.post("/call", json={"tool": "does_not_exist", "args": {}}, headers={"X-Mcp-Token": "test-token"})
    assert resp.status_code == 200
    data = resp.json()
    assert "error" in data


def test_tools_docs():
    os.environ["MCP_AUTH_TOKEN"] = "test-token"
    app = create_app()
    client = TestClient(app)
    resp = client.get("/tools/docs", headers={"X-Mcp-Token": "test-token"})
    assert resp.status_code == 200
    data = resp.json()
    assert "tools" in data
    assert isinstance(data["tools"], list)
    first = data["tools"][0]
    assert "name" in first and "description" in first
