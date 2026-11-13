# SQLHarness showcase and release preparation

**Goal:** Present SQLHarness through a reproducible synthetic optimization story, genuine dashboard screenshots, a clearer README introduction, and a GitHub draft release.

**Authorization:** The operator accepted the proposed screenshot/README package on 2026-11-13 and requested subsequent GitHub release preparation. No Tiny Tool Town submission. Prepare a draft, not a public release.

**Approach:** Keep application behavior unchanged. Seed a new isolated local journal with demo-only data and capture the actual dashboard. Reuse the existing journal schema and browser tooling. No database connections, real credentials, personal SQLHarness home, or runtime demo mode.

## Tasks

- [x] Create a small reproducible demo journal and document its use.
- [x] Capture and visually inspect three dashboard screenshots: comparison, version history, statement/table IO.
- [x] Improve the README introduction and minimal comparison example; retain detailed contracts.
- [x] Prepare listing copy and release notes without submitting to Tiny Tool Town.
- [ ] Verify documentation, demo reproducibility and screenshot content; run Windows `just verify` for release readiness.
- [ ] Commit the scoped package, push its branch, and prepare a GitHub draft release with matching archives and SHA256SUMS.

## Evidence

The journal contains one session, four operations (one timeout), three measured versions and one parameter case. `PRAGMA integrity_check` and foreign-key checks passed. Existing-home and invalid-port smoke checks reject input before overwriting anything.

`scripts/showcase/capture.mjs` completed against the actual Release dashboard with no browser/HTTP errors or API writes. The running application was built from `bf25ab58` in the main checkout (application source identical to this documentation branch), with Version=0.4.0; build succeeded with zero warnings/errors. Images were visually inspected. `docs/images/capture-report.json` records the browser, viewport, routes and asserted demo metrics. No SQL was executed. Only the newly created isolated homes under `D:/temp/sqlharness-showcase-home-20261113*` were used.

Windows gate: in progress at `D:/temp/sqlharness-showcase-verify-20261113-retry`. The first attempt hit an npm directory conflict; retry installed dependencies successfully. The same verify script is running directly under PowerShell without profiles after a nested shell startup stall. No gate or application source was changed.

Linux runtime and macOS runtime acceptance are outside this Windows session; cross-built archives must not be described as tested on those operating systems.

Independent whole-package review: no actionable findings. Reviewed schema/config compatibility, examples, links, all three screenshots, synthetic-data disclosure, token handling and GET-only browser routing. JavaScript syntax checks and `git diff --check` passed.
