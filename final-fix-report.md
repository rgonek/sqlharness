# Plan 058 final-fix report

## Result

The stored-plan reader now retains every measured run in its repetition inventory, including runs with no saved plan. The default selects the latest measured run and returns an explicit unavailable-evidence error if that run has no plan; it never falls back to an older repetition. Explicitly selecting an earlier repetition still returns its plan, reports all measured repetition numbers, leaves `planShapeStable` null, and includes an evidence gap when a later measured run lacks its saved plan.

The MCP matrix continuation test now compares recovered case indexes and parameter values directly in emitted order. README, MCP docs, the SQLHarness skill digest, capability metadata, and the capability byte-budget fixture describe the behavior.

## Evidence

- RED: `Plan_section_keeps_measured_runs_without_saved_plan_evidence_in_inventory` failed before the fix because no exception was raised for latest repetition 3; the old reader had silently selected repetition 2.
- Focused GREEN: Core 43 passed; MCP 29 passed. The capability discovery budget check measured 8,186 bytes under its 8,192-byte budget.
- Windows full gate: `pwsh ./scripts/verify.ps1` passed on `6fa9c7f185b3658c8858dcfe609df9d2fe7599f3`; Dashboard UI 148 passed, MCP 402 passed/4 skipped, Core 3,878 passed/11 skipped, build passed, format passed. Final marker: `verify: OK`.
- Linux full gate: `pwsh ./scripts/verify-linux.ps1` passed on the same SHA; Dashboard UI 148 passed, MCP 403 passed/3 skipped, Core 3,885 passed/4 skipped, build passed, format passed. Final marker: `verify-linux: OK`.
- Full gate logs: `.superpowers/sdd/058-benchmark-plan-evidence/final-fix-windows.log` and `.superpowers/sdd/058-benchmark-plan-evidence/final-fix-linux.log`.

## Commits and limits

- `986e647` — preserve measured plan evidence gaps and assert matrix continuation order.
- `6fa9c7f` — apply format fixes required by the full gate.

The first Windows run on `986e647` passed its tests but stopped at formatting diagnostics. Formatting was corrected and committed before rerunning either platform gate. Live SQL Server and PostgreSQL checks remain skipped; this work claims offline artifact behavior only.
