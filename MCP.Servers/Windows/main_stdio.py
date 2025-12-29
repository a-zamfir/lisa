"""Windows Automation MCP Server - stdio transport."""
from __future__ import annotations

import logging
import sys
from typing import Any

# Configure logging to stderr only (CRITICAL for stdio servers)
logging.basicConfig(
    level=logging.INFO,
    format='[%(name)s] %(levelname)s: %(message)s',
    stream=sys.stderr  # Never write to stdout in stdio servers!
)

from mcp.server.fastmcp import FastMCP
from mcp_server.services.tools_registry import (
    system_overview,
    top_processes,
    network_usage,
    power_state,
    process_inspector,
    startup_entries,
    large_files,
    find_duplicates,
    disk_usage_by_extension,
    recent_changes,
    file_metadata,
)

# Initialize FastMCP server
mcp = FastMCP("windows-automation")

# ============================================================================
# TOOL DEFINITIONS
# ============================================================================

@mcp.tool()
async def win_system_overview() -> dict[str, Any]:
    """Get comprehensive Windows system overview.

    Returns detailed information about:
    - CPU usage and load
    - Memory (total, free, used)
    - Disk usage per drive
    - Battery status (if applicable)
    - Active power scheme
    - System uptime
    """
    return system_overview()


@mcp.tool()
async def win_top_processes(count: int = 10) -> dict[str, Any]:
    """Get top processes by CPU or memory usage.

    Args:
        count: Number of top processes to return (default: 10, max: 50)
    """
    if count > 50:
        count = 50
    return top_processes({"count": count})


@mcp.tool()
async def win_network_usage() -> dict[str, Any]:
    """Get network adapter statistics and active connections.

    Returns information about:
    - Network adapters (sent/received bytes)
    - Active TCP/UDP connections
    - Listening ports
    """
    return network_usage()


@mcp.tool()
async def win_power_state() -> dict[str, Any]:
    """Get detailed power and battery information.

    Returns:
    - Battery status, percentage, estimated runtime
    - AC power status
    - Active power scheme
    - Sleep/hibernation settings
    """
    return power_state()


@mcp.tool()
async def win_process_inspector(pid: int) -> dict[str, Any]:
    """Inspect a specific Windows process by PID.

    Args:
        pid: Process ID to inspect

    Returns detailed information about:
    - Process name, path, command line
    - CPU and memory usage
    - Thread count
    - Parent process
    - User account
    """
    return process_inspector({"pid": pid})


@mcp.tool()
async def win_startup_entries() -> dict[str, Any]:
    """List all Windows startup entries.

    Returns programs configured to run at system startup from:
    - Registry (HKLM and HKCU Run keys)
    - Startup folders (All Users and Current User)
    - Scheduled tasks set to run at logon
    """
    return startup_entries()


@mcp.tool()
async def win_large_files(
    path: str,
    min_size_mb: int = 100,
    max_results: int = 50
) -> dict[str, Any]:
    """Find large files in a directory tree.

    Args:
        path: Directory path to search (e.g., "C:\\Users")
        min_size_mb: Minimum file size in MB (default: 100)
        max_results: Maximum number of results (default: 50, max: 200)
    """
    if max_results > 200:
        max_results = 200
    return large_files({
        "path": path,
        "min_size_mb": min_size_mb,
        "max_results": max_results
    })


@mcp.tool()
async def win_find_duplicates(
    path: str,
    min_size_mb: int = 1
) -> dict[str, Any]:
    """Find duplicate files by comparing file hashes.

    Args:
        path: Directory path to search (e.g., "C:\\Users\\Documents")
        min_size_mb: Minimum file size in MB to consider (default: 1)

    Returns groups of duplicate files with:
    - File paths
    - File size
    - Hash value
    - Potential space savings
    """
    return find_duplicates({
        "path": path,
        "min_size_mb": min_size_mb
    })


@mcp.tool()
async def win_disk_usage_by_extension(
    path: str,
    top_n: int = 20
) -> dict[str, Any]:
    """Analyze disk usage by file extension.

    Args:
        path: Directory path to analyze (e.g., "C:\\Users")
        top_n: Number of top extensions to return (default: 20)

    Returns statistics about:
    - Top file extensions by total size
    - File count per extension
    - Total space used by each extension
    """
    return disk_usage_by_extension({
        "path": path,
        "top_n": top_n
    })


@mcp.tool()
async def win_recent_changes(
    path: str,
    hours: int = 24,
    max_results: int = 100
) -> dict[str, Any]:
    """Find recently modified files.

    Args:
        path: Directory path to search (e.g., "C:\\Users\\Documents")
        hours: Look back this many hours (default: 24)
        max_results: Maximum number of results (default: 100, max: 500)
    """
    if max_results > 500:
        max_results = 500
    return recent_changes({
        "path": path,
        "hours": hours,
        "max_results": max_results
    })


@mcp.tool()
async def win_file_metadata(path: str) -> dict[str, Any]:
    """Get detailed metadata for a specific file.

    Args:
        path: Full path to the file (e.g., "C:\\Users\\file.txt")

    Returns:
    - File size, creation/modification times
    - File attributes (hidden, system, readonly, etc.)
    - NTFS permissions
    - File hash (MD5, SHA256)
    - Owner information
    """
    return file_metadata({"path": path})


# ============================================================================
# SERVER ENTRY POINT
# ============================================================================

def main():
    """Run the Windows Automation MCP server with stdio transport."""
    logging.info("Starting Windows Automation MCP server (stdio)")

    # Run with stdio transport (default for MCP)
    mcp.run(transport="stdio")


if __name__ == "__main__":
    main()
