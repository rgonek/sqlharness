#!/usr/bin/env python3
"""Block common agent access to protected SQLHarness settings and mutation routes."""

from __future__ import annotations

import argparse
import json
import re
import sys
from typing import Any


SQLHARNESS_FILE_PATH = re.compile(
    r"\.sqlharness[/\\]+(?P<name>dashboard\.json|dashboard-token|config\.json|targets\.json|\*\.json)(?=$|[\s\"'])",
    re.IGNORECASE,
)
READ_COMMAND = re.compile(r"^\s*(?:(?:sudo|command|builtin)\s+)*(?:cat|type|get-content|gc|more|less|head|tail)\b", re.IGNORECASE)
WRITE_COMMAND = re.compile(r"\b(?:set-content|out-file)\b", re.IGNORECASE)
REDIRECTION = re.compile(r"(?<![<>=])>{1,2}(?![=])")
MUTATION_API = re.compile(
    r"https?://(?:localhost|127\.0\.0\.1)(?::\d+)?/api/mutations(?:/|(?=$|[?#\s\"']))",
    re.IGNORECASE,
)
DENIAL_REASON = (
    "This SQLHarness settings read/write or loopback mutation API call is blocked by the agent hygiene hook. "
    "The hook protects against agent accidents; it is not isolation from a same-user adversary."
)


def should_block(command: str) -> bool:
    """Return whether a supported shell command reads, writes, or calls a protected target."""
    segments = re.split(r"[;&|\n]+", command)
    for segment in segments:
        paths = tuple(SQLHARNESS_FILE_PATH.finditer(segment))
        if not paths:
            continue
        names = {match.group("name").casefold() for match in paths}
        if READ_COMMAND.match(segment) and names.intersection({"dashboard.json", "dashboard-token", "*.json"}):
            return True
        if names.intersection({"config.json", "targets.json"}) and (
            WRITE_COMMAND.search(segment) or REDIRECTION.search(segment)
        ):
            return True
    return MUTATION_API.search(command) is not None


def should_block_file_tool(tool_name: str, tool_input: dict[str, Any]) -> bool:
    """Apply exact-path checks to common Read/Write/Edit tool payloads."""
    path = tool_input.get("file_path", tool_input.get("path"))
    if not isinstance(path, str):
        return False
    match = SQLHARNESS_FILE_PATH.search(path)
    if match is None:
        return False

    name = match.group("name").casefold()
    normalized_tool = tool_name.casefold()
    if normalized_tool in {"read", "read_file"}:
        return name in {"dashboard.json", "dashboard-token", "*.json"}
    if normalized_tool in {"write", "edit"}:
        return name in {"config.json", "targets.json"}
    return False


def response(client: str, block: bool) -> dict[str, Any]:
    if not block:
        return {}
    if client == "claude":
        return {
            "hookSpecificOutput": {
                "hookEventName": "PreToolUse",
                "permissionDecision": "deny",
                "permissionDecisionReason": DENIAL_REASON,
            }
        }
    return {"decision": "block", "reason": DENIAL_REASON}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--client", choices=("claude", "codex"), required=True)
    args = parser.parse_args()

    try:
        event = json.load(sys.stdin)
    except (json.JSONDecodeError, UnicodeDecodeError):
        print("{}")
        return 0

    tool_name = event.get("tool_name") if isinstance(event, dict) else None
    tool_input = event.get("tool_input") if isinstance(event, dict) else None
    block = False
    if isinstance(tool_name, str) and isinstance(tool_input, dict):
        if tool_name.casefold() in {"read", "read_file", "write", "edit"}:
            block = should_block_file_tool(tool_name, tool_input)
        elif tool_name.casefold() in {"bash", "exec_command"}:
            command = tool_input.get("command", tool_input.get("cmd"))
            block = isinstance(command, str) and should_block(command)
    print(json.dumps(response(args.client, block)))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
