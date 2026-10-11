import json
import subprocess
import sys
import unittest
from pathlib import Path


HOOK = Path(__file__).with_name("protect_sqlharness_config.py")


class ProtectSqlHarnessConfigHookTests(unittest.TestCase):
    def run_hook(self, client: str, command: str) -> tuple[int, dict]:
        event = {"tool_name": "Bash", "tool_input": {"command": command}}
        result = subprocess.run(
            [sys.executable, str(HOOK), "--client", client],
            input=json.dumps(event),
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertTrue(result.stdout, "hook must emit a JSON decision")
        return result.returncode, json.loads(result.stdout)

    def test_claude_blocks_glob_read_that_could_print_dashboard_token(self) -> None:
        code, output = self.run_hook("claude", "cat ~/.sqlharness/*.json")

        self.assertEqual(0, code)
        self.assertEqual("deny", output["hookSpecificOutput"]["permissionDecision"])

    def test_codex_blocks_windows_dashboard_config_read(self) -> None:
        code, output = self.run_hook("codex", r"type %USERPROFILE%\.sqlharness\dashboard.json")

        self.assertEqual(0, code)
        self.assertEqual("block", output["decision"])

    def test_blocks_posix_dashboard_token_read(self) -> None:
        for client, key in (("claude", "hookSpecificOutput"), ("codex", "decision")):
            with self.subTest(client=client):
                code, output = self.run_hook(client, "cat ~/.sqlharness/dashboard-token")

                self.assertEqual(0, code)
                self.assertIn(key, output)

    def test_blocks_windows_dashboard_token_read(self) -> None:
        code, output = self.run_hook("codex", r"Get-Content $env:USERPROFILE\.sqlharness\dashboard-token")

        self.assertEqual(0, code)
        self.assertEqual("block", output["decision"])

    def test_similarly_named_file_elsewhere_remains_allowed(self) -> None:
        code, output = self.run_hook("claude", "cat docs/dashboard-token-notes.md")

        self.assertEqual(0, code)
        self.assertEqual({}, output)

    def test_explicit_targets_file_read_remains_allowed(self) -> None:
        for client in ("claude", "codex"):
            with self.subTest(client=client):
                code, output = self.run_hook(client, "cat ~/.sqlharness/targets.json")

                self.assertEqual(0, code)
                self.assertEqual({}, output)


if __name__ == "__main__":
    unittest.main()
