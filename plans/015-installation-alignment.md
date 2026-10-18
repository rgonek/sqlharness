# Plan 015: Diagnose the divergence between the installed CLI and the repository

Status: **DONE — diagnostic stage only** (the divergence remains; not RESOLVED EXTERNALLY; the PATH binary was not replaced; no installation performed; evidence: `plans/015-installation-evidence.md`; commits `d2a0729..8e00431`, the end being a follow-up guard procedure; not merged; push pending)
Date: 2026-09-29. Base: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.
Priority: P3; effort: S; change risk: LOW.
Coverage: **PATH and stale help observation**. Dependencies: **005**.

## Context and execution instructions

Read the whole plan. Work in an isolated worktree; preserve other people's changes. Writing the plan is not an implementation. On completion, update plans/README.md together with the evidence; do not push or publish without an order.

Drift check:
```powershell
git status --short
git diff --stat 8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b..HEAD -- "plans/015-installation-evidence.md"
```

Predecessor changes are expected: read them and adapt the plan before coding. An unexplained divergence requires updating the assumptions, not mechanically pasting old code.

The PATH audit pointed to C:\Users\rgone\.local\bin\sqlharness.exe with a help lacking capabilities/validate/mcp, while the repository build supports MCP. This is an observation of the local installation, not a defect of the current source.

Reference point from the code/command:
```text
Get-Command sqlharness
```

.NET8/C#/xUnit. Match the patterns to the tests listed in the scope. Core stays the common place for safety; adapters do not create their own classifier. Use the existing fake reader/session/module and a synthetic SQLHARNESS_HOME. Do not read real secrets or user artifacts.

## Scope

- `plans/015-installation-evidence.md`

Additionally: new files named in the steps, the resulting plans/015-*.md documents and the status in the index. Other files require an explicit scope correction and justification before any change.

Out of scope: live DB, deploy, user profiles and passwords, installation, push, global disabling of validation and unrelated refactorings. This stage creates documents; it does not add commands, stubs or capabilities.

## Tasks

### 015/T1

- [x] Run Get-Command sqlharness -All, sqlharness --help and --version; compare with dotnet run --project src/SqlHarness.Cli -- --help and capabilities --json from the repository build. Do not display targets.json, env or profiles.

**Verification:** check the cited sources and local references; `git diff --check` → exit 0; the step's result recorded in the resulting document.

### 015/T2

- [x] Record in the evidence the origin, date/hash of the binary and the list of available commands. If the divergence has already disappeared, mark RESOLVED EXTERNALLY; do not reinstall.

**Verification:** check the cited sources and local references; `git diff --check` → exit 0; the step's result recorded in the resulting document.

### 015/T3

- [x] Given an existing divergence, prepare an exact update instruction with checksum verification and a copy of the previous binary. Performing the installation is a separate task; writing the plan does not mean replacing the PATH executable.

**Verification:** check the cited sources and local references; `git diff --check` → exit 0; the step's result recorded in the resulting document.

## Tests and completion

There is current evidence of the command's origin and of the divergence or of its disappearance; no profile/installation changes.

- [x] The resulting documents contain decisions, boundaries, the scope of future files and a test table.
- [x] Uncertainties marked UNPROVEN with the condition for obtaining evidence.
- [x] Do not run application tests when only writing documents.
- [x] `git diff --check` → exit 0.
- [x] `git status --short` shows only the author's changes within the scope.
- [x] The index contains the status, commit and evidence; local paths exist or are marked as new.
- [x] The absence of live/platform evidence is explicit.

Audit baseline: 1895 Core/CLI passed; MCP 122 passed, 2 timeouts, 4 skipped. Plan005 diagnoses the timeouts. An earlier safety fix may be ready for review given a documented independent gate failure; do not then announce a full PASS.

## Stop conditions and maintenance

Do not overwrite a working binary or user configuration in a diagnostic task.

If a step requires a real DB target, secrets or going beyond authorization, stop that step and record the absence. Complete the independent tasks. Do not mark the whole thing DONE without all required outcomes.

The reviewer checks negative-case safety, compatibility and the truthfulness of the evidence. After a change to the parser/SDK/operation, return to these tests. Small commits of the fix/test/docs type, without push. Required characteristic tests must precede a refactoring.
