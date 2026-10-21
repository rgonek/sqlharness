# Plan 090: Bound matcher title-backfill state Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans. Execute task-by-task with scoped review and final whole-branch review.

**Goal:** Bound Codex/OpenCode title-backfill bookkeeping by retained work without losing staged retries or interrupted scan progress.

**Architecture:** First establish which persisted operation and staged-call references legitimately keep a null-title identity live. Prune attempted and pending sets against that explicit live set during full sweeps, retaining existing strict targeted-cycle scope. Avoid an arbitrary size cap that silently drops live work.

**Tech Stack:** .NET 10, SQLite, xUnit.

**Spec:** [Fix contract below](#fix-contract), supplementing [084 design](../docs/superpowers/specs/2026-10-17-agent-sessions-design.md).

## Status and dependencies

- Task 1 implemented on `plan-090`; focused tests and branch formatting pass, Task 1 review is approved, and Windows `just verify` passed on product revision `1e6b0eb03330099d69495d4e59d478498ca27097`. Task 2 documentation and whole-branch reviews are pending. Depends on merged 084. Origin: deferred title-backfill memory Minor in [084 evidence](084-verification-evidence.md). Priority P2. No merge or push is authorized by this plan.

## Fix contract

- Full sweeps prune `_codexTitleBackfillAttempted` and `_codexTitleBackfillPending` using `(agent kind, conversation ID)` identities still requiring title enrichment and backed by retained Codex/OpenCode operations or the current in-flight batch snapshot. Persistent `agent_tool_calls` staging is Claude-only in the current writer; include staged references for Codex/OpenCode if a future writer adds them.
- A null-title conversation row alone is not proof of pending work. Already resolved titles and identities with neither retained operations nor current in-flight batch membership nor applicable live staged references leave both sets.
- In-flight batch identities stay live until the batch is applied or abandoned. Preserve resumable Codex index cursor progress; a pruned identity that later gains a retained operation can be attempted again. Persistent staged-only retention applies only to a kind with an actual staging writer.
- Apply pruning on full sweeps only. Targeted cycles read/enrich selected identities only and never consume unrelated pending sweep work. No transcript contents enter logs/tests beyond existing allowlisted fixture fields.
- No change to membership, annotation retention, frozen version labels, matching opt-in or MCP authorization. No persistent schema change unless actual inspection proves necessary; record a ruling and review any such expansion before implementation.

## Global Constraints

- Transcript reading stays in the dashboard, opt-in, bounded and allowlisted.
- Use actual operation/staging lifetimes, not conversation-cache existence or a wall-clock timeout, as retention evidence.
- Windows `just verify` and explicit branch formatting; Linux not required. No live transcript scans, merge or push authorized by this plan.

## Review Focus

1. Retention removes the final operation after the current batch captures it: pending retry survives through that in-flight batch and is pruned on the next full sweep. Persistent staged-only survival is conditional on an actual Codex/OpenCode staging writer.
2. Neither operation nor current in-flight batch membership nor applicable staged reference remains: attempted/pending identity is evicted despite a null-title cache row.
3. Interrupted index scan: progress resumes and unrelated work is not consumed by a targeted cycle.
4. Same conversation ID for different agent kinds: pruning is kind-scoped.
5. Repeated operation retention cycles: bookkeeping converges to live work rather than historical identities.

### Task 1: Lifetime evidence and pruning regression

**Files:** `src/SqlHarness.Dashboard/Transcripts/TranscriptMatcher.cs`; `tests/SqlHarness.Tests/Dashboard/Transcripts/TranscriptMatcherTests.cs`; retention fixtures in `tests/SqlHarness.Tests/Journal/` only if required by the actual staging path.

**Interfaces:** Keep existing matcher entry points unchanged. Extract a private live-identity query/helper only if needed; it consumes the existing sweep snapshot and current batch, returns kind-qualified identities. No public diagnostic API solely for testing.

- [x] Inspect the exact staged-reference table/expiration path and existing set/cursor ownership. Record concrete table/column references and expected lifetime in the implementation report before edits.
- [x] Add RED regressions for in-flight batch survival after operation retention, orphan null-title eviction, post-eviction reintroduction, kind separation, interrupted scans, and repeated retention cycles. Persistent staged-only coverage is conditional on a Codex/OpenCode staging writer; the current writer stages Claude only. Observe set bounds through an internal test seam or established matcher tests, not reflection over incidental field names.
- [x] Run `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~TranscriptMatcherTests"`; retain meaningful RED output.
- [x] Implement sweep pruning against retained operations and the in-flight batch snapshot, preserving applicable staged references if a writer exists; rerun GREEN and self-review query/output bounds. Commit `fix(090): prune title-backfill state against live matcher work`.

### Task 2: Verification and closure

**Files:** `plans/090-verification-evidence.md`, this plan, `plans/README.md`, `plans/history.md`.

- [ ] Review Task 1 and whole branch. Record deterministic before/after live-set sizes for many retired identities plus staged survivors; no memory-performance claim from a passing fixture alone.
- [x] Run `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef main`, retain direct exit/log; run Windows `just verify` with unique logs on final product tree. Direct branch formatting passed; Windows `just verify` exited 0 on product revision `1e6b0eb03330099d69495d4e59d478498ca27097` (UI 361; Core 4,599 passed/11 skipped; MCP 489 passed/4 skipped; 0 build warnings/errors; 9m04s).
- [ ] Record revision/counts, staging lifetime evidence and any remaining limits; close the deferred 084 Minor only when its regression passes. Commit `docs(090): record title-backfill lifecycle verification`.

## Plan self-review

All review-focus cases belong to Task 1; Task 2 records the full gate. Staging details deliberately require source inspection, while the required lifetime policy is fixed here. Implementation must not replace that investigation with unconditional eviction.
