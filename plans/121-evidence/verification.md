# Plan 121 verification

Windows `just verify` exited 0 on 2026-10-25. All five stages succeeded or were
appropriately skipped: unchanged dependency install skipped, build passed with
zero .NET compiler warnings/errors, UI check passed, .NET tests passed, and formatting skipped
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
not run and are not claimed. Integration and local publication are recorded below.

## Local integration and publication

`main` fast-forwarded from `05a414a4` to `fc83e808`, including implementation
commit `95eb43b7`. Its source/test/script trees exactly match the verified candidate;
there is no additional product merge delta and no separate post-fast-forward gate
is claimed. The final gate already included current main's Plan 116 changes.

Before temporarily clearing the overlapping plan-index file, raw copies, a patch
and SHA-256 values were saved. A three-way text merge combined the plan update
with the user's changes. The user's original patch was verified unchanged after
integration, and the root README's raw bytes were identical. No implicit stash or
unrelated-user-work commit was used. Details: [integration receipt](integration.json).

`just publish fast` exited 0 from main and installed
`C:\Users\rgone\.local\bin\sqlharness.exe`. The installed SHA-256 exactly matches
the new self-contained artifact, and the installed executable's `--version` smoke
exited 0. Version remains the repository's existing `0.0.0`; no version bump was
requested. See [publication receipt](publication.json) and [publish log](logs/publish.txt).
Vite retains its non-fatal large-chunk notice. Existing processes were not stopped
or restarted; new dashboard processes use the new layout. No push was performed.

The follow-up integration/publication documentation commit changes no product code;
the published product tree remains the current main product tree.
