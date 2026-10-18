# Plan 014: Prepare an executable pg_stat_statements diagnostics contract

> **REJECTED 2026-10-11** — `pgstop` will not be built. `query` can already read `pg_stat_statements`; a ranked example query in the agent docs (plan 029) covers the need. Kept for history; do not execute.

Status: **DONE — design/diagnostic stage only** (the `pgstop` feature is still **PLANNED**; commits `f0934b3..7d82bf1`; unticked task fields are not a status)
Contract update: 2026-10-11 at `729d88f`: cumulative ranking v1, runtime probe of capabilities; delta deferred. Original stage date: 2026-09-29. Base: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.
Priority: P2; effort: L; change risk: MED.
Coverage: **PG diagnostics proposal**. Dependencies: **003, 004, 009**.

## Context and execution instructions

Read the whole plan. Work in an isolated worktree; preserve other people's changes. Writing the plan is not an implementation. On completion, update plans/README.md together with the evidence; do not push or publish without an order.

Drift check:
```powershell
git status --short
git diff --stat 8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b..HEAD -- "plans/014-pg-statements-contract.md" "plans/014-pg-statements-implementation.md"
```

Predecessor changes are expected: read them and adapt the plan before coding. An unexplained divergence requires updating the assumptions, not mechanically pasting old code.

The spec docs/superpowers/specs/2026-09-26-postgres-statement-diagnostics.md describes PG14+, cumulative counters and delta. However, it requires recognizing an unknown version before connect and assumes broader reset detectability than the global stats_reset alone can prove. The outcome is now a cumulative v1 ranking with a runtime probe of required names/types; exact version-number pairs are not a gate. Delta remains deferred outside the v1 implementation.

Reference point from the code/command:
```text
QueryStoreAvailable: !pg
```

.NET8/C#/xUnit. Match the patterns to the tests listed in the scope. Core stays the common place for safety; adapters do not create their own classifier. Use the existing fake reader/session/module and a synthetic SQLHARNESS_HOME. Do not read real secrets or user artifacts.

## Scope

- `plans/014-pg-statements-contract.md`
- `plans/014-pg-statements-implementation.md`

Additionally: new files named in the steps, the resulting plans/014-*.md documents and the status in the index. Other files require an explicit scope correction and justification before any change.

Out of scope: live DB, deploy, user profiles and passwords, installation, push, global disabling of validation and unrelated refactorings. This stage creates documents; it does not add commands, stubs or capabilities.

## Tasks

### 014/T1

- [ ] Check the official PG14/15/16 documentation and record the matrix of column availability, extension version and selective reset detection capability. Do not equate the server version with the extension version. Cite the sources in the document.

**Verification:** check the cited sources and local references; `git diff --check` → exit 0; the step's result recorded in the resulting document.

### 014/T2

- [ ] Design the resolve/auth/identity/version/extension/runtime column probe sequence. Read the version after target confirmation; check the required names/types independently of the exact number pair. Qualify the extension object safely. An incompatible required shape, a missing extension or missing ranking permissions is exit 5; optional metrics/context unavailable with a reason.

**Verification:** check the cited sources and local references; `git diff --check` → exit 0; the step's result recorded in the resulting document.

### 014/T3

- [ ] Keep a contract separate from qstop: no window and recency, current DB filters, deterministic ranking, SQL only in the local queries.jsonl. Establish the meaning of a hidden queryid and the identity of the aggregation; do not merge unknown IDs into a false single query.

**Verification:** check the cited sources and local references; `git diff --check` → exit 0; the step's result recorded in the resulting document.

### 014/T4

- [ ] Record the deferral of delta beyond v1. Do not implement Compare, a snapshot store, nor the unknown/incomparable tokens themselves without a useful difference. A possible later plan for the observed difference must describe reset/eviction/restart and undetectable loss of continuity; the reset matrix does not block cumulative ranking.

**Verification:** check the cited sources and local references; `git diff --check` → exit 0; the step's result recorded in the resulting document.

### 014/T5

- [ ] Write the implementation plan for PgStatementTopQuery/Reader/ArtifactWriter, the pgstop CLI and a possible new inspect kind, with shared Core, scope and gate. Fixture-first for required/optional shape, PG14/15/16/17/18 and an unknown number with a compatible minimum, without a pair allowlist; no extension installation, no GUC changes. Live proof remains a separate step requiring an explicit target.

**Verification:** check the cited sources and local references; `git diff --check` → exit 0; the step's result recorded in the resulting document.

## Tests and completion

The contract distinguishes observable facts from unknown, and has a fixture/error/empty/runtime-shape/version table; delta is not a v1 task and not a standalone code plan. No new advertised capabilities before implementation.

- [ ] The resulting documents contain decisions, boundaries, the scope of future files and a test table.
- [ ] Uncertainties marked UNPROVEN with the condition for obtaining evidence.
- [ ] Do not run application tests when only writing documents.
- [ ] `git diff --check` → exit 0.
- [ ] `git status --short` shows only the author's changes within the scope.
- [ ] The index contains the status, commit and evidence; local paths exist or are marked as new.
- [ ] The absence of live/platform evidence is explicit.

Audit baseline: 1895 Core/CLI passed; MCP 122 passed, 2 timeouts, 4 skipped. Plan005 diagnoses the timeouts. An earlier safety fix may be ready for review given a documented independent gate failure; do not then announce a full PASS.

## Stop conditions and maintenance

Design stage; the reset questions remain a source-level UNPROVEN and do not block cumulative v1. Do not invent continuity markers, nor add Compare.

If a step requires a real DB target, secrets or going beyond authorization, stop that step and record the absence. Complete the independent tasks. Do not mark the whole thing DONE without all required outcomes.

The reviewer checks negative-case safety, compatibility and the truthfulness of the evidence. After a change to the parser/SDK/operation, return to these tests. Small commits of the fix/test/docs type, without push. Required characteristic tests must precede a refactoring.
