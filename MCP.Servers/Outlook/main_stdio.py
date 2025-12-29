"""Outlook MCP Server - stdio transport.

Provides tools for interacting with Microsoft Outlook:
- Calendar: Get and set events
- Contacts: Get contacts
- Email: Get emails with filters, send emails
"""
from __future__ import annotations

import logging
import sys
from datetime import datetime, timedelta
from typing import Any, Optional

# Configure logging to stderr only (CRITICAL for stdio servers)
logging.basicConfig(
    level=logging.INFO,
    format='[%(name)s] %(levelname)s: %(message)s',
    stream=sys.stderr
)

from mcp.server.fastmcp import FastMCP

# Initialize FastMCP server
mcp = FastMCP("outlook-integration")

# Outlook COM constants
OL_MAIL_ITEM = 0
OL_APPOINTMENT_ITEM = 1
OL_CONTACT_ITEM = 2
OL_FOLDER_INBOX = 6
OL_FOLDER_CALENDAR = 9
OL_FOLDER_CONTACTS = 10
OL_FOLDER_SENT_MAIL = 5


def _get_outlook():
    """Get Outlook application COM object."""
    import pythoncom
    import win32com.client

    # Initialize COM for the current thread
    try:
        pythoncom.CoInitialize()
    except Exception:
        pass  # Already initialized

    # Try connecting to running Outlook first
    try:
        return win32com.client.GetActiveObject("Outlook.Application")
    except Exception:
        pass

    # Fall back to creating/connecting via Dispatch
    try:
        return win32com.client.Dispatch("Outlook.Application")
    except Exception as ex:
        raise RuntimeError(
            f"Failed to connect to Outlook: {ex}. "
            "Make sure Outlook is running and both Outlook and LISA "
            "are running with the same privileges (both admin or both non-admin)."
        )


def _get_namespace():
    """Get Outlook MAPI namespace."""
    outlook = _get_outlook()
    return outlook.GetNamespace("MAPI")


def _format_datetime(dt) -> str:
    """Format COM datetime to ISO string."""
    if dt is None:
        return ""
    try:
        if hasattr(dt, 'isoformat'):
            return dt.isoformat()
        return str(dt)
    except Exception:
        return str(dt)


def _parse_datetime(dt_str: str) -> datetime:
    """Parse datetime string to datetime object."""
    # Try common formats
    formats = [
        "%Y-%m-%dT%H:%M:%S",
        "%Y-%m-%d %H:%M:%S",
        "%Y-%m-%d %H:%M",
        "%Y-%m-%d",
    ]
    for fmt in formats:
        try:
            return datetime.strptime(dt_str, fmt)
        except ValueError:
            continue
    raise ValueError(f"Cannot parse datetime: {dt_str}")


# ============================================================================
# CALENDAR TOOLS
# ============================================================================

@mcp.tool()
async def outlook_get_calendar_events(
    days_ahead: int = 7,
    days_back: int = 0,
    max_results: int = 50
) -> dict[str, Any]:
    """Get calendar events from Outlook.

    Args:
        days_ahead: Number of days to look ahead (default: 7)
        days_back: Number of days to look back (default: 0)
        max_results: Maximum events to return (default: 50, max: 200)
    """
    if max_results > 200:
        max_results = 200

    try:
        namespace = _get_namespace()
        calendar = namespace.GetDefaultFolder(OL_FOLDER_CALENDAR)

        start_date = datetime.now() - timedelta(days=days_back)
        end_date = datetime.now() + timedelta(days=days_ahead)

        # Filter appointments by date range
        items = calendar.Items
        items.Sort("[Start]")
        items.IncludeRecurrences = True

        restriction = f"[Start] >= '{start_date.strftime('%m/%d/%Y')}' AND [Start] <= '{end_date.strftime('%m/%d/%Y')}'"
        filtered = items.Restrict(restriction)

        events = []
        count = 0
        for item in filtered:
            if count >= max_results:
                break
            try:
                events.append({
                    "subject": item.Subject,
                    "start": _format_datetime(item.Start),
                    "end": _format_datetime(item.End),
                    "location": getattr(item, 'Location', ''),
                    "body_preview": (item.Body or "")[:200],
                    "is_recurring": getattr(item, 'IsRecurring', False),
                    "organizer": getattr(item, 'Organizer', ''),
                    "required_attendees": getattr(item, 'RequiredAttendees', ''),
                })
                count += 1
            except Exception:
                continue

        return {
            "events": events,
            "count": len(events),
            "date_range": {
                "start": start_date.isoformat(),
                "end": end_date.isoformat()
            }
        }
    except Exception as ex:
        return {"error": str(ex), "events": []}


@mcp.tool()
async def outlook_create_calendar_event(
    subject: str,
    start: str,
    end: str,
    location: Optional[str] = None,
    body: Optional[str] = None,
    attendees: Optional[str] = None
) -> dict[str, Any]:
    """Create a new calendar event in Outlook.

    Args:
        subject: Event title/subject
        start: Start datetime (ISO format: YYYY-MM-DD HH:MM or YYYY-MM-DDTHH:MM:SS)
        end: End datetime (ISO format)
        location: Event location (optional)
        body: Event description/body (optional)
        attendees: Semicolon-separated email addresses (optional)
    """
    try:
        outlook = _get_outlook()
        appointment = outlook.CreateItem(OL_APPOINTMENT_ITEM)

        appointment.Subject = subject
        appointment.Start = _parse_datetime(start)
        appointment.End = _parse_datetime(end)

        if location:
            appointment.Location = location
        if body:
            appointment.Body = body
        if attendees:
            appointment.RequiredAttendees = attendees
            appointment.MeetingStatus = 1  # olMeeting

        appointment.Save()

        return {
            "success": True,
            "message": f"Event '{subject}' created successfully",
            "event": {
                "subject": subject,
                "start": start,
                "end": end,
                "location": location or "",
            }
        }
    except Exception as ex:
        return {"success": False, "error": str(ex)}


# ============================================================================
# CONTACTS TOOLS
# ============================================================================

@mcp.tool()
async def outlook_get_contacts(
    search: Optional[str] = None,
    max_results: int = 50
) -> dict[str, Any]:
    """Get contacts from Outlook.

    Args:
        search: Optional search term to filter contacts by name or email
        max_results: Maximum contacts to return (default: 50, max: 200)
    """
    if max_results > 200:
        max_results = 200

    try:
        namespace = _get_namespace()
        contacts_folder = namespace.GetDefaultFolder(OL_FOLDER_CONTACTS)
        items = contacts_folder.Items

        contacts = []
        count = 0

        for item in items:
            if count >= max_results:
                break

            try:
                # Check if it's a contact item
                if item.Class != 40:  # olContact
                    continue

                full_name = getattr(item, 'FullName', '') or ''
                email = getattr(item, 'Email1Address', '') or ''
                company = getattr(item, 'CompanyName', '') or ''

                # Apply search filter if provided
                if search:
                    search_lower = search.lower()
                    if (search_lower not in full_name.lower() and
                        search_lower not in email.lower() and
                        search_lower not in company.lower()):
                        continue

                contacts.append({
                    "full_name": full_name,
                    "email": email,
                    "email2": getattr(item, 'Email2Address', '') or '',
                    "business_phone": getattr(item, 'BusinessTelephoneNumber', '') or '',
                    "mobile_phone": getattr(item, 'MobileTelephoneNumber', '') or '',
                    "company": company,
                    "job_title": getattr(item, 'JobTitle', '') or '',
                })
                count += 1
            except Exception:
                continue

        return {
            "contacts": contacts,
            "count": len(contacts),
            "search_term": search or ""
        }
    except Exception as ex:
        return {"error": str(ex), "contacts": []}


# ============================================================================
# EMAIL TOOLS
# ============================================================================

@mcp.tool()
async def outlook_get_emails(
    folder: str = "inbox",
    max_results: int = 25,
    sender: Optional[str] = None,
    subject_contains: Optional[str] = None,
    days_back: int = 7,
    unread_only: bool = False
) -> dict[str, Any]:
    """Get emails from Outlook with various filters.

    Args:
        folder: Folder to search - 'inbox' or 'sent' (default: inbox)
        max_results: Maximum emails to return (default: 25, max: 100)
        sender: Filter by sender email or name (optional)
        subject_contains: Filter by subject containing this text (optional)
        days_back: Only get emails from last N days (default: 7)
        unread_only: Only return unread emails (default: False)
    """
    if max_results > 100:
        max_results = 100

    try:
        namespace = _get_namespace()

        # Select folder
        if folder.lower() == "sent":
            mail_folder = namespace.GetDefaultFolder(OL_FOLDER_SENT_MAIL)
        else:
            mail_folder = namespace.GetDefaultFolder(OL_FOLDER_INBOX)

        items = mail_folder.Items
        items.Sort("[ReceivedTime]", True)  # Sort by newest first

        # Build restriction filter
        cutoff_date = datetime.now() - timedelta(days=days_back)
        restriction = f"[ReceivedTime] >= '{cutoff_date.strftime('%m/%d/%Y')}'"

        if unread_only:
            restriction += " AND [UnRead] = True"

        filtered = items.Restrict(restriction)

        emails = []
        count = 0

        for item in filtered:
            if count >= max_results:
                break

            try:
                # Additional Python-side filtering
                sender_name = getattr(item, 'SenderName', '') or ''
                sender_email = getattr(item, 'SenderEmailAddress', '') or ''
                subject = getattr(item, 'Subject', '') or ''

                # Apply sender filter
                if sender:
                    sender_lower = sender.lower()
                    if (sender_lower not in sender_name.lower() and
                        sender_lower not in sender_email.lower()):
                        continue

                # Apply subject filter
                if subject_contains:
                    if subject_contains.lower() not in subject.lower():
                        continue

                body = getattr(item, 'Body', '') or ''
                emails.append({
                    "subject": subject,
                    "sender_name": sender_name,
                    "sender_email": sender_email,
                    "received": _format_datetime(item.ReceivedTime),
                    "body_preview": body[:300] if body else "",
                    "unread": getattr(item, 'UnRead', False),
                    "has_attachments": getattr(item, 'Attachments', None) is not None and item.Attachments.Count > 0,
                    "importance": getattr(item, 'Importance', 1),  # 0=Low, 1=Normal, 2=High
                })
                count += 1
            except Exception:
                continue

        return {
            "emails": emails,
            "count": len(emails),
            "folder": folder,
            "filters": {
                "sender": sender,
                "subject_contains": subject_contains,
                "days_back": days_back,
                "unread_only": unread_only
            }
        }
    except Exception as ex:
        return {"error": str(ex), "emails": []}


@mcp.tool()
async def outlook_send_email(
    to: str,
    subject: str,
    body: str,
    cc: Optional[str] = None,
    bcc: Optional[str] = None,
    importance: str = "normal"
) -> dict[str, Any]:
    """Send an email via Outlook.

    Args:
        to: Recipient email address(es), semicolon-separated for multiple
        subject: Email subject line
        body: Email body text
        cc: CC recipients, semicolon-separated (optional)
        bcc: BCC recipients, semicolon-separated (optional)
        importance: Email importance - 'low', 'normal', or 'high' (default: normal)
    """
    try:
        outlook = _get_outlook()
        mail = outlook.CreateItem(OL_MAIL_ITEM)

        mail.To = to
        mail.Subject = subject
        mail.Body = body

        if cc:
            mail.CC = cc
        if bcc:
            mail.BCC = bcc

        # Set importance
        importance_map = {"low": 0, "normal": 1, "high": 2}
        mail.Importance = importance_map.get(importance.lower(), 1)

        mail.Send()

        return {
            "success": True,
            "message": f"Email sent successfully to {to}",
            "details": {
                "to": to,
                "subject": subject,
                "cc": cc or "",
                "bcc": bcc or "",
            }
        }
    except Exception as ex:
        return {"success": False, "error": str(ex)}


@mcp.tool()
async def outlook_search_emails(
    query: str,
    max_results: int = 25,
    folder: str = "inbox"
) -> dict[str, Any]:
    """Search emails using Outlook's search functionality.

    Args:
        query: Search query (searches subject, body, sender)
        max_results: Maximum results to return (default: 25, max: 100)
        folder: Folder to search - 'inbox' or 'sent' (default: inbox)
    """
    if max_results > 100:
        max_results = 100

    try:
        namespace = _get_namespace()

        if folder.lower() == "sent":
            mail_folder = namespace.GetDefaultFolder(OL_FOLDER_SENT_MAIL)
        else:
            mail_folder = namespace.GetDefaultFolder(OL_FOLDER_INBOX)

        # Use Outlook's built-in search
        items = mail_folder.Items
        items.Sort("[ReceivedTime]", True)

        query_lower = query.lower()
        emails = []
        count = 0

        for item in items:
            if count >= max_results:
                break

            try:
                subject = getattr(item, 'Subject', '') or ''
                body = getattr(item, 'Body', '') or ''
                sender = getattr(item, 'SenderName', '') or ''

                # Search in subject, body, and sender
                if (query_lower in subject.lower() or
                    query_lower in body.lower() or
                    query_lower in sender.lower()):

                    emails.append({
                        "subject": subject,
                        "sender_name": sender,
                        "sender_email": getattr(item, 'SenderEmailAddress', '') or '',
                        "received": _format_datetime(item.ReceivedTime),
                        "body_preview": body[:300] if body else "",
                        "unread": getattr(item, 'UnRead', False),
                    })
                    count += 1
            except Exception:
                continue

        return {
            "emails": emails,
            "count": len(emails),
            "query": query,
            "folder": folder
        }
    except Exception as ex:
        return {"error": str(ex), "emails": []}


# ============================================================================
# SERVER ENTRY POINT
# ============================================================================

def main():
    """Run the Outlook MCP server with stdio transport."""
    logging.info("Starting Outlook Integration MCP server (stdio)")
    mcp.run(transport="stdio")


if __name__ == "__main__":
    main()
