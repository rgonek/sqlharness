# SQLHarness showcase and release preparation

**Goal:** Present SQLHarness through a reproducible synthetic optimization story, genuine dashboard screenshots, a clearer README introduction, and a GitHub draft release.

**Authorization:** The operator accepted the proposed screenshot/README package on 2026-11-13 and requested subsequent GitHub release preparation. No Tiny Tool Town submission. Prepare a draft, not a public release.

**Status:** COMPLETE; locally integrated into `main` by fast-forward to `e967bdd1` on 2026-11-13 at the operator's explicit request. [GitHub draft v0.4.0](https://github.com/rgonek/sqlharness/releases/tag/untagged-9ac45435c8f858fa9e5f).

**Approach:** Keep application behavior unchanged. Seed a new isolated local journal with demo-only data and capture the actual dashboard. Reuse the existing journal schema and browser tooling. No database connections, real credentials, personal SQLHarness home, or runtime demo mode.

## Tasks

- [x] Create a small reproducible demo journal and document its use.
- [x] Capture and visually inspect three dashboard screenshots: comparison, version history, statement/table IO.
- [x] Improve the README introduction and minimal comparison example; retain detailed contracts.
- [x] Prepare listing copy and release notes without submitting to Tiny Tool Town.
- [x] Verify documentation, demo reproducibility and screenshot content; run the Windows verification gate for release readiness.
- [x] Commit the scoped package, push its branch, and prepare a GitHub draft release with matching archives and SHA256SUMS.

## Evidence

The journal contains one session, four operations (one timeout), three measured versions and one parameter case. `PRAGMA integrity_check` and foreign-key checks passed. Existing-home and invalid-port smoke checks reject input before overwriting anything.

`scripts/showcase/capture.mjs` completed against the actual Release dashboard with no browser/HTTP errors or API writes. The running application was built from `bf25ab58` in the main checkout (application source identical to this documentation branch), with Version=0.4.0; build succeeded with zero warnings/errors. Images were visually inspected. `docs/images/capture-report.json` records the browser, viewport, routes and asserted demo metrics. No SQL was executed. Only the newly created isolated homes under `D:/temp/sqlharness-showcase-home-20261113*` were used.

Windows gate: **PASS**, exit 0, at `D:/temp/sqlharness-showcase-verify-20261113-retry`: UI 570, Core 4919 passed / 11 skipped, MCP 537 passed / 4 skipped; format skipped with zero changed C# files. Runtime 17m15s, including a cold dependency install. Initial `just verify` was interrupted during installation; an owned orphan npm process was stopped after a directory conflict, then the unchanged `scripts/verify.ps1` entry point was invoked directly under PowerShell without profiles. No gate or application source was changed. The passing UI check reports existing React hook lint warnings and Node DEP0190, not new failures.

Linux runtime and macOS runtime acceptance are outside this Windows session; cross-built archives must not be described as tested on those operating systems.

Independent whole-package review: no actionable findings. Reviewed schema/config compatibility, examples, links, all three screenshots, synthetic-data disclosure, token handling and GET-only browser routing. JavaScript syntax checks and `git diff --check` passed.

Release builds: `ef7084b820fb22cdada273623a63c1faeddac596`, version 0.4.0. Self-contained, single-file, untrimmed publishes succeeded for win-x64, linux-x64 and osx-arm64, using a separate MSBuild artifacts directory. Outputs and logs are under `D:/temp/sqlharness-release-v0.4.0-20261113*`. Windows archive extraction retained the published executable's SHA-256; extracted binary reported 0.4.0. The actual Windows single-file executable served all three screenshot routes and the asserted comparison metrics, without browser/HTTP errors or API writes (`D:/temp/sqlharness-release-smoke-20261113/capture-report.json`). Unix archive listings and skill ZIP contents were inspected. Each binary archive contains LICENSE and README.txt. SHA256SUMS covers the four distributable archives.

GitHub draft verified with `isDraft=true`, target `ef7084b820fb22cdada273623a63c1faeddac596`, and exactly five uploaded assets. GitHub's SHA-256 digest and byte size matched every local asset, including SHA256SUMS. No remote `v0.4.0` tag exists, so tag-triggered release publication was not started. The later evidence/notes commit changes documentation only; binaries remain tied to their exact build revision. No public release, Tiny Tool Town submission, main integration, installed-binary replacement or personal-home mutation occurred. The demo dashboard and inspection browser were stopped; isolated evidence homes remain available.

## Local main integration

The operator subsequently requested the merge. Both checkouts were clean; after `git fetch origin`, local main and origin/main matched at `bf25ab58`. `git merge --ff-only docs/showcase-release` advanced main to `e967bdd1` without conflicts. Scoped checks in `D:/Dev/sqlharness` passed: `git diff --check`, syntax checks for both showcase scripts, 27 local documentation links, and a fresh demo seed with SQLite integrity/foreign-key checks in `D:/temp/sqlharness-showcase-merge-20261113`. Application, tests, workflows and dependency configuration are unchanged from the previously verified product. The documentation-only verification rule applies; the full gate was not repeated. No push of main or release publication was requested or performed. The managed showcase worktree and remote branch remain available for the draft's documentation links.

## Publication follow-up

The operator explicitly requested pushing main and finishing the release. Main was pushed at `648e9882`. Version 0.4.0 remains appropriate for the next pre-1.0 release; the notes explicitly identify incompatible CLI changes since 0.3.1 rather than promising API stability.

Before publication, inspection of GitHub CI run 43418903568 revealed three help-parser failures on both platforms and one additional Linux fixture failure, despite the local Windows gate passing. The help failures reproduced locally with `CI=true`, `GITHUB_ACTIONS=true`, `TERM=xterm-256color`, and no inherited NO_COLOR: all three failed. Child-process `NO_COLOR=1` fixes the test parsers without changing CLI behavior. The Linux fixture used a literal Windows artifact path; it now uses `Path.Combine(Path.GetTempPath(), "artifact-001")`, preserving its expected basename. A focused run of command-surface, docs-sync and benchmark-summary tests passed 34/34 under the simulated Actions environment. Independent focused review found no actionable issues. Logs: `D:/temp/sqlharness-release-ci-repro-actions.log` and `D:/temp/sqlharness-release-ci-fixed.log`. Application source and already verified release binaries are unchanged. Remote CI and publication remain in progress.
