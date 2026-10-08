#!/usr/bin/env python3
"""Block common shell reads that could expose SQLHarness dashboard credentials."""

from __future__ import annotations

import argparse
import json
import re
import sys
from typing import Any


SENSITIVE_CONFIG_PATH = re.compile(r"\.sqlharness[/\\](?:dashboard\.json|\*\.json)(?=$|[\s\"'])", re.IGNORECASE)
READ_COMMAND = re.compile(r"^\s*(?:(?:sudo|command|builtin)\s+)*(?:cat|type|get-content|gc|more|less|head|tail)\b", re.IGNORECASE)
DENIAL_REASON = (
    "Reading dashboard.json or globbed SQLHarness JSON config files is blocked because "
    "dashboard.json contains a live access token. Read an explicitly named targets.json if needed."
)


def should_block(command: str) -> bool:
    """Return whether a supported shell read command targets protected config."""
    if not SENSITIVE_CONFIG_PATH.search(command):
        return False
    return any(READ_COMMAND.match(segment) for segment in re.split(r"[;&|\n]+", command))


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
    command = tool_input.get("command") if isinstance(tool_input, dict) else None
    block = tool_name in ("Bash", "bash") and isinstance(command, str) and should_block(command)
    print(json.dumps(response(args.client, block)))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
