# Task 12 report: agent-side hygiene hook

## RED

The requested command, `python -m pytest scripts/hooks -q`, could not start: the active Python and the installed Python 3.10 both report `No module named pytest`. No package was installed. The standard-library runner was used to observe the tests fail before implementation:

```powershell
python -m unittest discover -s scripts/hooks -p 'test_*.py' -v
```

Result before implementation: 16 tests ran; 3 failed and 10 errored on the new missing blocks. Existing hook behavior and the allowed `targets.json`, unrelated filename, and unrelated localhost route cases passed.

## GREEN

```powershell
python -m unittest discover -s scripts/hooks -p 'test_*.py' -v
```

Result after implementation: 17 tests passed. Coverage includes Read/read_file, Write/Edit, shell redirection, PowerShell `Set-Content` and `Out-File`, Claude Bash, Codex `exec_command` with `cmd`, malformed JSON fail-open behavior, and the allowed target/negative path cases.

## Files

- `scripts/hooks/protect_sqlharness_config.py`: path-scoped blocks for dashboard credential reads and settings writes, plus loopback mutation API calls.
- `scripts/hooks/test_protect_sqlharness_config.py`: subprocess JSON tests using fixture paths and commands only; no operator files or credentials were read.
- `plans/073-mutation-approval-broker.md`: Task 12 checklist.

## Scope and behavior

The mutation route is matched on `localhost` or `127.0.0.1` at any port because the brief names the loopback host and `/api/mutations/` path but does not specify a port. Matching is bounded to that path; unrelated localhost paths remain allowed. The existing fail-open behavior for malformed JSON remains covered. WebAuthn credentials and routes remain deferred to Plan 077. This hook is an agent-accident guardrail, not isolation from a same-user adversary.

`git diff --check` was run before commit. The pytest command remains unverified because pytest is not installed; the full plan gate and Linux gate are outside Task 12, and the Linux gate was explicitly waived.
