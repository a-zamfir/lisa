---
name: outlook
description: Outlook calendar, email, and contacts operations on Windows. Use when the user asks to read or create calendar events, read or search emails, or look up contacts via Outlook.
---

# Outlook

Use this skill to work with Outlook calendar, email, and contacts.

## Run

Run a tool by invoking the runner and pass arguments as JSON on stdin:

- Command: `python scripts/run.py --tool <tool-id>`
- Help: `python scripts/run.py --help`
- Input JSON: `{"args": { ... }}` (object)
- Output JSON: `{ "success": true, "data": { ... }, "error": null, "meta": { ... } }` or `{ "success": false, "data": null, "error": "..." }`

## Available scripts

- `scripts/run.py` - skill entrypoint that dispatches to a specific Outlook tool.
- `scripts/logic.py` - Outlook mail/calendar/contact tool implementations.

## Workflow

1. Activate this skill when the user asks about Outlook mail, calendar, or contacts.
2. Choose the narrowest matching tool from `tools.json`.
3. Run `python scripts/run.py --tool <tool-id>` and pass only the required JSON args on stdin.
4. For write actions, wait for approval before execution and report the final structured result clearly.

## Tools

- `outlook_get_calendar_events`: List calendar events in a date range.
- `outlook_create_calendar_event`: Create a new calendar event.
- `outlook_get_contacts`: Search contacts.
- `outlook_get_emails`: List emails with filters (folder/sender/subject/unread).
- `outlook_send_email`: Send an email.
- `outlook_search_emails`: Full-text search across emails.

## Notes

- Require Outlook desktop app and matching privilege level (admin vs. normal).
- Keep all execution inside the repo; use the runner to enforce safety.
- See `tools.json` for argument schemas and approval requirements.
