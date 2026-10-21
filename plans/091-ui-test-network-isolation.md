# Plan 091: Eliminate unintended network requests in UI tests Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans. Execute task-by-task with scoped review and final whole-branch review.

**Goal:** Remove unexplained localhost:3000 ECONNREFUSED diagnostics from successful dashboard UI tests while preserving meaningful error-path coverage.

**Architecture:** Trace each unexpected request to its test and request owner, then provide explicit fixtures and clean up query/router lifecycles. Fail unexpected requests at the responsible test boundary rather than filtering console errors or requiring a real local server.

**Tech Stack:** React, TanStack Query/Router, Vitest, existing dashboard test harness; Windows gate.

**Spec:** [Fix contract below](#fix-contract). Evidence of the deferred diagnostic is in [084 verification](084-verification-evidence.md).

## Status and dependencies

- IN PROGRESS — Task 1 implementation and Windows verification are complete; the final Sol low whole-branch review is pending. Depends on merged 084. Priority P2, test-only hygiene. No new dependency assumed.

## Fix contract

- Identify the source test, request method/path and lifecycle trigger of every unexpected localhost:3000 request in the current full UI check. Preserve the original output and distinguish intentionally tested failures from leaked background requests.
- Unit/component tests do not depend on a listening dashboard server. Supply explicit response fixtures for requests the scenario owns, and cancel/clear background work on teardown using the existing harness.
- Unexpected HTTP requests fail with method/path and originating test context. Apply interception at the actual request boundary (fetch/XHR/router as the investigation establishes), without globally hiding error output or weakening intentional failure assertions.
- Error-path tests assert their errors explicitly; expected diagnostics are scoped to those tests and restored afterward. No global console.error suppression, process-level uncaught-error ignore, blind timeout increases or sleeps.
- Product behavior, API contracts and dependency versions remain unchanged. If a product defect is found, report it separately and obtain a bounded implementation decision; do not hide it with fixtures.
- This plan does not claim to fix the separate large-diff test timeout recorded during 084; investigate it only if evidence shows the same request/lifecycle leak causes it.

## Global Constraints

- Preserve ordinary user-facing error handling and the existing size-limit assertions.
- Reuse existing test infrastructure before adding dependencies. No live DB, dashboard server, transcript scan or credentials needed.
- Windows full gate on final product tree; Linux only on explicit request. No merge/push implied.

## Review Focus

1. Requests start after test teardown: owning test must await/cancel them, not leak into another test.
2. Query retries/refetch or router preload: fixtures cover intended work without turning off tested behavior globally.
3. Intentional API failure: assertions survive and diagnostics stay local.
4. Full suite order differs from isolated execution: no shared handler/cache state leaks.
5. An unmocked request is introduced: the test fails clearly without a socket connection attempt.

### Task 1: Diagnose and isolate unintended requests

**Files:** actual affected UI test files under `src/SqlHarness.Dashboard/ui/src/`, existing setup/config under that UI directory; investigate `pages/AgentSessionPage.test.tsx`, `pages/SessionsPage.test.tsx` and `components/SessionVersionDiff.test.tsx` without assuming they are the source. Shared helper may be added at `src/SqlHarness.Dashboard/ui/src/test/network.ts` if needed.

**Interfaces:** Existing test harness and production query APIs remain compatible. If shared interception is needed, its lifecycle is test-scoped install/reset/restore, with explicit per-test handlers and an error for unmatched requests.

- [ ] Run `npm --prefix src/SqlHarness.Dashboard/ui run check`, capture the exact diagnostic and identify request owners with test-scoped tracing. Record concrete offending files and method/path before changing fixtures.
- [ ] Add a RED regression proving an unexpected request fails locally with method/path; add cleanup/isolation coverage for the actual offending lifecycle and preserve an intentional API-error test.
- [ ] Implement explicit fixtures and teardown at the smallest owning scope; remove temporary tracing after root cause is documented. Never silence the original error globally.
- [ ] Rerun affected tests GREEN, then full UI check; verify no unexpected ECONNREFUSED or unhandled rejections. Commit `test(091): isolate dashboard test network activity`.

### Task 2: Full gate and diagnostic evidence

**Files:** `plans/091-verification-evidence.md`, this plan, queue/history; test helper documentation only if a reusable helper was introduced.

- [x] Review Task 1 evidence and confirm interception wraps the global `fetch` boundary before each test, explicit route matching is required, and teardown cleans the DOM, cancels tracked QueryClients, asserts retained unexpected-request diagnostics, and restores `fetch` in `finally`. Task 1 scoped review is approved; final Sol low whole-branch review remains pending.
- [x] Run Windows `just verify` with unique persistent logs. Record every pass/skip count and inspect UI output; the unexplained request diagnostics are absent.
- [x] Record before/after request evidence, exact test owners and tested revision in [verification evidence](091-verification-evidence.md). The Plan 084 UI diagnostic Minor is closed by the passing UI log with no `ECONNREFUSED`, unhandled rejection, or unmatched-request diagnostic.
- [ ] Complete final Sol low whole-branch review and any required docs-only follow-up.

Task 2 evidence commit: `docs(091): record UI network-isolation verification`. The full gate does not need to be repeated for subsequent documentation-only review amendments.

## Plan self-review

All five review-focus cases are covered by Task 1; Task 2 verifies full-suite integration. This is a diagnosis-led test change, not a speculative console filter or general test-runner rewrite.
