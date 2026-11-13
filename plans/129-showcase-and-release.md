# SQLHarness showcase and release preparation

**Goal:** Present SQLHarness through a reproducible synthetic optimization story, genuine dashboard screenshots, a clearer README introduction, and a GitHub draft release.

**Authorization:** The operator accepted the screenshot/README package, then explicitly requested main integration, push and release publication on 2026-11-13. No Tiny Tool Town submission.

**Status:** COMPLETE; main pushed and [v0.4.0 published as Latest](https://github.com/rgonek/sqlharness/releases/tag/v0.4.0). Release tag and all binary builds target `4ce7f7f16b684dde88905d71fa674e854168d1fd`. Historical draft evidence below is superseded by the final publication record.

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

The complete original Linux failure list included five benchmark-summary cases sharing that Windows-path fixture, plus three further platform-dependent test assumptions. Run 43443877762 confirmed the initial parser/path fixes and isolated the remaining three: an open SQLite connection did not enforce managed FileShare exclusion on Unix, record equality compared launch-argument collection identities, and the HTTP test server reconstructed CRLF headers with native AppendLine. Tests now acquire an explicit exclusive managed file handle, compare executable and argument contents separately, and preserve canonical CRLF in the captured HTTP request. Original security/body assertions remain intact. Independent review caught the related Content-Length parsing issue before the final CRLF fix. The final focused Windows run passed 110 with one Unix-only skip, including all affected test classes (`D:/temp/sqlharness-release-portability-final.log`). No application source changes.

Both Unix release archives were repackaged with executable mode 0755 and documentation mode 0644. Extracted binary SHA-256 values match the original published outputs; SHA256SUMS and all five GitHub asset digests were reverified after upload.

## Final publication — 2026-11-13

The operator authorized push and publication. Version 0.4.0 continues the pre-1.0 sequence; the CLI/MCP contracts still evolve. Main and the release source were pushed. The public release is Latest, not a prerelease; its tag resolves to `4ce7f7f16b684dde88905d71fa674e854168d1fd`.

Final [GitHub CI run 43498191993](https://github.com/rgonek/sqlharness/actions/runs/43498191993) passed on Windows and Linux for that exact revision. The integrated Windows `just verify` gate previously passed in 11m43s at `D:/temp/sqlharness-release-final-verify-20261113` (UI 570, Core 4919 passed / 11 skipped, MCP 537 passed / 4 skipped); subsequent changes affected test fixtures only. Final CI verifies the complete release tree. Independent review found no outstanding findings.

Retained earlier failures exposed platform-dependent fixtures, pre-existing formatting diagnostics, resource-sensitive hosted-runner tests and bounded-path assertions. Formatting changed 54 files without behavioral changes. Three resource-sensitive test classes now run without unrelated class concurrency, retaining their internal concurrency assertions; cold PowerShell startup allows 60 seconds. Hosted-runner root cause was not proven. Core passed separately with two logical processors (4919/11). A long TEMP prefix reproduced six failures in 29 MCP reader tests; bounded assertions and short synthetic rejection roots then passed 29/29, with production budgets/redaction unchanged and the known-secret assertion strengthened. Full MCP passed 537/4. The final Pester fixture accepts a numeric, nonnegative elapsed duration because rounding can legitimately produce 0.0; local Pester passed 84/84. Original failed-run evidence remains available.

Final self-contained archives are under `D:/temp/sqlharness-release-v0.4.0-4ce7f7f1-20261113/assets`. All five uploaded assets matched local SHA-256 digests before publication. Windows archive extraction, executable hash, version/build ID, embedded-dashboard routes and metrics, and MCP initialization/tool discovery/capabilities passed. These checks used isolated synthetic homes and no database connection. Unix archives preserve executable mode 0755. Linux CI ran its native tests and published-server smoke; the exact cross-built Linux/macOS download binaries were not launched on those operating systems. macOS runtime acceptance remains unverified.

The public Windows download checksum was verified after publication. The release contains Windows x64, Linux x64 and macOS arm64 archives, the skill ZIP and SHA256SUMS. No Tiny Tool Town submission was made.
Publishing the draft created the tag and triggered redundant Release workflow 43501491386. It was canceled after public publication/checksum verification to avoid a second release-creation attempt; final main CI 43498191993 passed.
