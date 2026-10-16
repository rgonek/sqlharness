import json
import subprocess
import sys
import unittest
from pathlib import Path


HOOK = Path(__file__).with_name("protect_sqlharness_config.py")


class ProtectSqlHarnessConfigHookTests(unittest.TestCase):
    def run_hook(self, client: str, command: str) -> tuple[int, dict]:
        return self.run_event(client, "Bash", {"command": command})

    def run_event(self, client: str, tool_name: str, tool_input: dict) -> tuple[int, dict]:
        event = {"tool_name": tool_name, "tool_input": tool_input}
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

    def test_read_tool_blocks_dashboard_files(self) -> None:
        for filename in ("dashboard.json", "dashboard-token"):
            with self.subTest(filename=filename):
                code, output = self.run_event(
                    "codex", "Read", {"file_path": f"C:/fixture/.sqlharness/{filename}"}
                )

                self.assertEqual(0, code)
                self.assertEqual("block", output["decision"])

    def test_read_file_tool_accepts_path_argument_and_blocks_dashboard_token(self) -> None:
        code, output = self.run_event(
            "claude", "read_file", {"path": "/fixture/.sqlharness/dashboard-token"}
        )

        self.assertEqual(0, code)
        self.assertEqual("deny", output["hookSpecificOutput"]["permissionDecision"])

    def test_read_tool_allows_targets_json(self) -> None:
        code, output = self.run_event(
            "codex", "Read", {"file_path": "C:/fixture/.sqlharness/targets.json"}
        )

        self.assertEqual(0, code)
        self.assertEqual({}, output)

    def test_write_and_edit_tools_block_settings_json(self) -> None:
        for tool_name in ("Write", "Edit"):
            with self.subTest(tool_name=tool_name):
                code, output = self.run_event(
                    "codex", tool_name, {"file_path": "C:/fixture/.sqlharness/config.json"}
                )

                self.assertEqual(0, code)
                self.assertEqual("block", output["decision"])

    def test_write_tool_accepts_path_argument_for_targets_json(self) -> None:
        code, output = self.run_event(
            "claude", "Write", {"path": "/fixture/.sqlharness/targets.json"}
        )

        self.assertEqual(0, code)
        self.assertEqual("deny", output["hookSpecificOutput"]["permissionDecision"])

    def test_shell_redirection_and_powershell_file_writes_are_blocked(self) -> None:
        commands = (
            "printf x > ~/.sqlharness/config.json",
            "Set-Content -Path $env:USERPROFILE\\.sqlharness\\targets.json -Value x",
            "'x' | Out-File -FilePath ~/.sqlharness/config.json",
        )
        for command in commands:
            with self.subTest(command=command):
                code, output = self.run_hook("claude", command)

                self.assertEqual(0, code)
                self.assertIn("hookSpecificOutput", output)

    def test_shell_command_blocks_loopback_mutation_api_at_any_port(self) -> None:
        for command in (
            "curl -X POST http://127.0.0.1:4319/api/mutations/approve",
            "curl -X POST http://localhost:9876/api/mutations/request",
        ):
            with self.subTest(command=command):
                code, output = self.run_hook("codex", command)

                self.assertEqual(0, code)
                self.assertEqual("block", output["decision"])

    def test_exec_command_with_cmd_blocks_loopback_mutation_api(self) -> None:
        code, output = self.run_event(
            "codex",
            "exec_command",
            {"cmd": "curl -X POST http://localhost:4319/api/mutations/approve"},
        )

        self.assertEqual(0, code)
        self.assertEqual("block", output["decision"])

    def test_claude_bash_mutation_api_is_blocked(self) -> None:
        code, output = self.run_event(
            "claude",
            "Bash",
            {"command": "curl -X POST http://127.0.0.1:4319/api/mutations/approve"},
        )

        self.assertEqual(0, code)
        self.assertEqual("deny", output["hookSpecificOutput"]["permissionDecision"])

    def test_other_localhost_port_path_and_other_loopback_path_are_allowed(self) -> None:
        for command in (
            "curl http://localhost:4319/api/health",
            "curl http://127.0.0.1:4319/api/other/mutations/approve",
        ):
            with self.subTest(command=command):
                code, output = self.run_hook("codex", command)

                self.assertEqual(0, code)
                self.assertEqual({}, output)

    def test_malformed_json_fails_open(self) -> None:
        result = subprocess.run(
            [sys.executable, str(HOOK), "--client", "codex"],
            input="{malformed",
            capture_output=True,
            text=True,
            check=False,
        )

        self.assertEqual(0, result.returncode)
        self.assertEqual({}, json.loads(result.stdout))


if __name__ == "__main__":
    unittest.main()
