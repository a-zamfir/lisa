---
name: agent-browser
description: Browser automation for web navigation, page snapshots, element interaction, and targeted extraction. Use when the user wants LISA to inspect or operate a website through a local browser session.
---

# Agent Browser

Use this skill for browser-based workflows where the model needs to inspect a live page, navigate the web, click elements, fill fields, or extract structured page content.

## Run

Run a tool by invoking the runner and pass arguments as JSON on stdin:

- Command: `python scripts/run.py --tool <tool-id>`
- Help: `python scripts/run.py --help`
- Input JSON: `{"args": { ... }}`
- Output JSON: `{ "success": true, "data": { ... }, "error": null, "meta": { ... } }` or `{ "success": false, "data": null, "error": "..." }`

## Available scripts

- `scripts/run.py` - skill entrypoint that dispatches to a specific browser tool.
- `scripts/browser_cli.py` - local wrapper around the repo-pinned `agent-browser` CLI.

## Workflow

1. Activate this skill when the user needs browser automation or website inspection.
2. Open a URL first, then snapshot the page before interacting.
3. Prefer `agent_browser_snapshot` after every meaningful page change so the model works from fresh refs.
4. Use element refs returned by snapshot output for clicks, fills, and targeted extraction.
5. Keep browser actions scoped to the task and close the session when done.

## Tools

- `agent_browser_open` - Open a URL in a named browser session.
- `agent_browser_snapshot` - Capture the current page state as JSON, including interactive refs.
- `agent_browser_click` - Click an element ref.
- `agent_browser_fill` - Fill a form field by ref.
- `agent_browser_press` - Send a key press such as `Enter`.
- `agent_browser_wait` - Wait for an element, text, URL, load state, or fixed delay.
- `agent_browser_get_text` - Extract text from an element ref.
- `agent_browser_get_attr` - Extract an attribute value from an element ref.
- `agent_browser_close` - Close the current browser/session.

## Notes

- This skill depends on a repo-local pinned `agent-browser` npm package.
- LISA executes the installed repo-local CLI shim from `node_modules/.bin/agent-browser.cmd` through the `native-cli` skill runner mode.
- Browser sessions are local to this machine.
- Interactive actions can change remote state; keep approval requirements aligned with the risk of the operation.
- See `tools.json` for argument schemas, approval requirements, and timeouts.
