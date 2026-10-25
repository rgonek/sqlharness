# Plan 121 verification

Windows `just verify` exited 0 on 2026-10-25. All five stages succeeded or were
appropriately skipped: unchanged dependency install skipped, build passed with
zero warnings/errors, UI check passed, .NET tests passed, and formatting skipped
because no C# source changed.

- UI: 67 files, **381 passed**; typecheck and lint passed.
- Core/Dashboard/CLI: **4718 passed**, 11 existing platform/live skips.
- MCP: **535 passed**, 4 existing platform/live skips.
- Source tree: `e32b0f540e92b98afcd604c2d661f7aef1b3755c`.
- .NET test tree: `bf4fece77afdba3a307054e56f7a108f0f110971`.
- Current main (`05a414a4`, Plan 116 documentation) was incorporated before this gate.
- `git diff --cached --check` passed. Product source was unchanged during the gate.

## Test scheduling and failed attempts

Two preceding standard parallel runs each failed one unchanged timing test:
`PidPublicationError_PreservesOriginalWhenMarkerWriteFails` (5-second test wait),
then `Request_timeout_returns_Failed_within_the_shared_deadline` (1-second elapsed
assertion). Both exact tests passed isolated (719 ms and 388 ms). The two affected
classes passed together, 28/28, with collection parallelism off.

The final full gate used assembly-specific `xunit.runner.json` files only in the
ignored Debug test output directories: `parallelizeTestCollections: false`,
`maxParallelThreads: 1`, `diagnosticMessages: true`. Logs confirm collection
parallelism off. Every existing test, explicit concurrency assertion, timeout and
filter remained unchanged; no backend or tracked test-runner policy was modified.
The Core and MCP projects still ran together as the gate normally schedules them.
This is successful verification under reduced host contention, not proof that the
original parallel runner is now stable. Configuration follows the
[official xUnit runner documentation](https://xunit.net/docs/config-xunit-runner-json).

The isolated results, failed parallel logs, local settings and final gate logs are
retained in [logs](logs/). The original first UI iteration failure remains in the
machine-local `sqlharness-layout-121-gate` directory; it was not acceptance evidence.

## Boundaries

Independent standards and specification reviews approved the corrected source;
see [reviews](reviews.md). No browser surface was available, so visual/responsive
browser comparison remains unverified. Linux and live database acceptance were
not run and are not claimed. Integration and local publication are recorded next.
