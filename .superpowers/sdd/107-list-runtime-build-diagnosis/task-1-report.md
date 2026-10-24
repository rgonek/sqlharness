# Task 1 report: establish evidence and build identity

Status: DONE_WITH_CONCERNS

## Result

The assigned worktree began at `3c698588a32372ce82f83ad3e79fe15e9fac2e87` on `plan-107`, clean. Plan 107's historical planned base is `677b2f93b66c06d3dc0bda8475c7570ef29738d9`; repeating its scoped-file drift diff produced no output. The older runtime observation separately names source snapshot `fdc161d1`.

The available PATH executable is `C:\Users\rgone\.local\bin\sqlharness.exe`, SHA-256 `51F566567C76E4D1696AC2DE6CD1AE58B69E338D58E7838894A5A9E3C8283FCC`, ProductVersion `0.0.0-dev+1b8bd70b4020ffdb217d50ee8c5a3b7a17e36162`, and `--version` output `0.0.0`. Its embedded commit predates 1,504 insertions and 88 deletions in the seven scoped source/test files between that commit and inspected HEAD. This is evidence that the available executable candidate was built from an older source revision.

Fifteen running `sqlharness` processes reported that same executable image path, with starts on 2026-10-23 from 07:31:44 through 17:18:20 local time. Process command lines and configuration were not read. Module enumeration showed the apphost and system modules, not a separate SqlHarness assembly. The exact mapped file hash/build for those processes remains unresolved; no source-to-loaded-binary identity is claimed.

The retained offline request shapes and their expected source behavior and previously observed errors are recorded in the evidence file. No request was replayed and no live database was accessed.

## Commands and results

1. Read `task-1-brief.md`, `AGENTS.md`, `plans/README.md`, `docs/agents/verification.md`, only Plan 107's Global constraints section, and the two named observations.
2. `git status --short --branch`, `git rev-parse HEAD`, `git worktree list --porcelain`: assigned worktree was clean at `3c698588`; main was at `6d543e5` with the unrelated pre-existing README and untracked plan changes listed in the evidence.
3. Repeated the plan's exact `git diff --stat 677b2f9..HEAD -- <seven scoped paths>` command: no output.
4. Inspected candidate metadata with `Get-Command`, `Get-FileHash`, `Get-Item`, and `VersionInfo`; ran the candidate's `--version` command, which printed `0.0.0`.
5. Queried only process ID, image path, start time, and filtered module paths for existing `sqlharness` processes. Did not inspect process command lines, configs, credentials or target state.
6. `git show -s --format=fuller 1b8bd70...` and `git diff --stat 1b8bd70...HEAD -- <seven scoped paths>`: candidate version commit exists and is older than substantial scoped source changes.

## Files changed

- `plans/107-evidence/task-1-build-identity.md` — source/build identity, bounded process evidence, retained synthetic request record, and worktree state.
- `.superpowers/sdd/107-list-runtime-build-diagnosis/task-1-report.md` — this report.

No production code or tests changed. Verification gate and tests were not run because this task changes diagnostic documentation/evidence only; no test was requested. Self-review: inspect the final diff and confirm only these two files changed before commit.

## Concerns and limits

- The current on-disk candidate reports an older source commit, but process image path and start time do not prove the exact binary bytes mapped by each existing process. Loaded build identity remains unresolved.
- The known offline errors are retained observations, not a reproduction performed in this task. Runtime cause classification remains for later plan tasks.
- No installation, restart, live SQL, historical workload replay, or configuration/credential access occurred.

## Commit

The task changes are committed on `plan-107`; the completion message reports the resulting commit ID.
