"""Outlook skill logic."""
from __future__ import annotations

import logging
from datetime import datetime, timedelta
from typing import Any, Optional

logger = logging.getLogger(__name__)

OL_MAIL_ITEM = 0
OL_APPOINTMENT_ITEM = 1
OL_CONTACT_ITEM = 2
OL_FOLDER_INBOX = 6
OL_FOLDER_CALENDAR = 9
OL_FOLDER_CONTACTS = 10
OL_FOLDER_SENT_MAIL = 5


def _get_outlook():
    import pythoncom
    import win32com.client

    try:
        pythoncom.CoInitialize()
    except Exception:
        pass

    try:
        return win32com.client.GetActiveObject("Outlook.Application")
    except Exception:
        pass

    try:
        return win32com.client.Dispatch("Outlook.Application")
    except Exception:
        pass

    try:
        return win32com.client.gencache.EnsureDispatch("Outlook.Application")
    except Exception as ex:
        raise RuntimeError(
            f"Failed to connect to Outlook: {ex}. "
            "Ensure Outlook is installed. If Outlook is running, try running "
            "both Outlook and LISA with the same privileges (both admin or both normal)."
        )


def _get_namespace():
    outlook = _get_outlook()
    return outlook.GetNamespace("MAPI")


def _format_datetime(dt) -> str:
    if dt is None:
        return ""
    try:
        if hasattr(dt, "isoformat"):
            return dt.isoformat()
        return str(dt)
    except Exception:
        return str(dt)


def _parse_datetime(dt_str: str) -> datetime:
    formats = [
        "%Y-%m-%dT%H:%M:%S",
        "%Y-%m-%d %H:%M:%S",
        "%Y-%m-%dT%H:%M",
        "%Y-%m-%d %H:%M",
        "%Y-%m-%d",
    ]

    parsed_dt = None
    for fmt in formats:
        try:
            parsed_dt = datetime.strptime(dt_str, fmt)
            break
        except ValueError:
            continue

    if parsed_dt is None:
        raise ValueError(f"Cannot parse datetime: {dt_str}")

    return parsed_dt


async def outlook_get_calendar_events(
    days_ahead: int = 7,
    days_back: int = 0,
    max_results: int = 50
) -> dict[str, Any]:
    if max_results > 200:
        max_results = 200

    try:
        namespace = _get_namespace()
        calendar = namespace.GetDefaultFolder(OL_FOLDER_CALENDAR)

        start_date = datetime.now() - timedelta(days=days_back)
        end_date = datetime.now() + timedelta(days=days_ahead)

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
                    "location": getattr(item, "Location", ""),
                    "body_preview": (item.Body or "")[:200],
                    "is_recurring": getattr(item, "IsRecurring", False),
                    "organizer": getattr(item, "Organizer", ""),
                    "required_attendees": getattr(item, "RequiredAttendees", ""),
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


async def outlook_create_calendar_event(
    subject: str,
    start: str,
    end: str,
    location: Optional[str] = None,
    body: Optional[str] = None,
    attendees: Optional[str] = None
) -> dict[str, Any]:
    try:
        namespace = _get_namespace()
        calendar = namespace.GetDefaultFolder(OL_FOLDER_CALENDAR)
        calendar_name = getattr(calendar, "Name", "Calendar")
        store = getattr(calendar, "Store", None)
        store_name = getattr(store, "DisplayName", "")
        store_id = getattr(store, "StoreID", "")

        start_local = _parse_datetime(start)
        end_local = _parse_datetime(end)
        logger.info(
            "Creating calendar event in store='%s' calendar='%s' start='%s' end='%s'",
            store_name,
            calendar_name,
            start_local.strftime("%m/%d/%Y %H:%M"),
            end_local.strftime("%m/%d/%Y %H:%M"),
        )

        appointment = calendar.Items.Add(OL_APPOINTMENT_ITEM)
        appointment.Subject = subject
        appointment.Start = start_local
        appointment.End = end_local

        if location:
            appointment.Location = location
        if body:
            appointment.Body = body
        if attendees:
            appointment.RequiredAttendees = attendees
            appointment.MeetingStatus = 1

        appointment.Save()
        entry_id = getattr(appointment, "EntryID", "") or ""
        if entry_id:
            logger.info("Calendar event saved entry_id='%s'", entry_id)
        else:
            logger.warning("Calendar event saved but EntryID missing.")

        return {
            "success": True,
            "message": f"Event '{subject}' created successfully",
            "event": {
                "subject": subject,
                "start": start,
                "end": end,
                "location": location or "",
                "entry_id": entry_id,
                "store": store_name,
                "store_id": store_id,
                "calendar": calendar_name,
                "start_local": start_local.isoformat(),
                "end_local": end_local.isoformat(),
            }
        }
    except Exception as ex:
        logger.exception("Failed to create calendar event: %s", ex)
        return {"success": False, "error": str(ex)}


async def outlook_get_contacts(
    search: Optional[str] = None,
    max_results: int = 50
) -> dict[str, Any]:
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
                if item.Class != 40:
                    continue

                full_name = getattr(item, "FullName", "") or ""
                email = getattr(item, "Email1Address", "") or ""
                company = getattr(item, "CompanyName", "") or ""

                if search:
                    search_lower = search.lower()
                    if (search_lower not in full_name.lower() and
                        search_lower not in email.lower() and
                        search_lower not in company.lower()):
                        continue

                contacts.append({
                    "full_name": full_name,
                    "email": email,
                    "email2": getattr(item, "Email2Address", "") or "",
                    "business_phone": getattr(item, "BusinessTelephoneNumber", "") or "",
                    "mobile_phone": getattr(item, "MobileTelephoneNumber", "") or "",
                    "company": company,
                    "job_title": getattr(item, "JobTitle", "") or "",
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


async def outlook_get_emails(
    folder: str = "inbox",
    max_results: int = 25,
    sender: Optional[str] = None,
    subject_contains: Optional[str] = None,
    days_back: int = 7,
    unread_only: bool = False
) -> dict[str, Any]:
    if max_results > 100:
        max_results = 100

    try:
        namespace = _get_namespace()
        if folder.lower() == "sent":
            mail_folder = namespace.GetDefaultFolder(OL_FOLDER_SENT_MAIL)
        else:
            mail_folder = namespace.GetDefaultFolder(OL_FOLDER_INBOX)

        items = mail_folder.Items
        items.Sort("[ReceivedTime]", True)

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
                sender_name = getattr(item, "SenderName", "") or ""
                sender_email = getattr(item, "SenderEmailAddress", "") or ""
                subject = getattr(item, "Subject", "") or ""

                if sender:
                    sender_lower = sender.lower()
                    if (sender_lower not in sender_name.lower() and
                        sender_lower not in sender_email.lower()):
                        continue

                if subject_contains:
                    if subject_contains.lower() not in subject.lower():
                        continue

                body = getattr(item, "Body", "") or ""
                emails.append({
                    "subject": subject,
                    "sender_name": sender_name,
                    "sender_email": sender_email,
                    "received": _format_datetime(item.ReceivedTime),
                    "body_preview": body[:300] if body else "",
                    "unread": getattr(item, "UnRead", False),
                    "has_attachments": getattr(item, "Attachments", None) is not None and item.Attachments.Count > 0,
                    "importance": getattr(item, "Importance", 1),
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


async def outlook_send_email(
    to: str,
    subject: str,
    body: str,
    cc: Optional[str] = None,
    bcc: Optional[str] = None,
    importance: str = "normal"
) -> dict[str, Any]:
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


async def outlook_search_emails(
    query: str,
    max_results: int = 25,
    folder: str = "inbox"
) -> dict[str, Any]:
    if max_results > 100:
        max_results = 100

    try:
        namespace = _get_namespace()
        if folder.lower() == "sent":
            mail_folder = namespace.GetDefaultFolder(OL_FOLDER_SENT_MAIL)
        else:
            mail_folder = namespace.GetDefaultFolder(OL_FOLDER_INBOX)

        items = mail_folder.Items
        items.Sort("[ReceivedTime]", True)

        query_lower = query.lower()
        emails = []
        count = 0

        for item in items:
            if count >= max_results:
                break

            try:
                subject = getattr(item, "Subject", "") or ""
                body = getattr(item, "Body", "") or ""
                sender = getattr(item, "SenderName", "") or ""

                if (query_lower in subject.lower() or
                    query_lower in body.lower() or
                    query_lower in sender.lower()):

                    emails.append({
                        "subject": subject,
                        "sender_name": sender,
                        "sender_email": getattr(item, "SenderEmailAddress", "") or "",
                        "received": _format_datetime(item.ReceivedTime),
                        "body_preview": body[:300] if body else "",
                        "unread": getattr(item, "UnRead", False),
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
