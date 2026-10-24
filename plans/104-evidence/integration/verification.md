# Plan 104 integration verification

Status: combined product verified; controller main fast-forward pending.

- Main parent: `9718b052f40335d3647cd96750d80431248f9140` (Plan 105 integrated).
- Feature parent: `e5c9ed70e2fc8bdaf35bde36cffabb1f38f1c07c`.
- Tested combined merge: `d2f2b060f140052dbb1713ceb66ad18720ae9923`. Subsequent evidence commits do not change `src`, `tests` or `docs`.
- Command: `just verify -LogDirectory D:/temp/sqlharness-plan104-main-integration-verify`.
- Final direct exit: **0**, five stages, 9m53s. Build: 0 warnings/errors; UI: 372 passed; Core: 4717 passed/11 skipped; MCP: 524 passed/4 skipped.
- Gate skipped UI installation (unchanged lockfile) and working-tree formatting (no C# edits). The separate post-merge [branch format](branch-format.log) checked 21 changed C# files against `9718b052`, [direct exit 0](branch-format.exit).

Fresh final logs: [console/direct exit](verification/console.log), [build](verification/build.log), [UI](verification/ui-check.log), [Core](verification/test-SqlHarness.Tests.log), [MCP](verification/test-SqlHarness.Mcp.Tests.log), [exit](verification/gate.exit).

[Implementation and conflict resolution](implementation-report.md), [Luna xhigh integration review](task-review.md), and [Sol low whole-integration review](whole-review.md) are approved with no Critical/Important findings. Their nonblocking discoverability note is deferred: compact benchmark descriptions omit an explicit metrics retrieval route, but the artifact tool's section description and documentation expose it; diagnostics remain optional. The cost is a possible additional discovery lookup, not unavailable evidence or a mandatory artifact call.

The [first combined gate](verification-attempt1/console.log) failed only on unchanged `ProcessRunnerTests.PidPublicationError_PreservesOriginalWhenMarkerWriteFails` timing out at its five-second wait. The test and Auth source are identical to main parent `9718b052`. An [isolated exact-test retry](pid-publication-repro.log) passed without code edits, then the complete gate above passed. The timing cause is unproven; this is not a claim to have fixed that test or its runtime. Earlier failures are retained separately from final GREEN.

The controller preserved the six dirty/untracked main files byte-for-byte at `D:/temp/sqlharness-plan104-main-preserve-20261024` with SHA-256 manifest and the original working patch. Main fast-forward and restoration will be recorded below. The originally untracked Plan 104 will remain available in that backup when the implemented plan takes its tracked path.

No push, installation, Linux, installed-binary or implementation live-DB acceptance is claimed. The experimental SQL Server recognition rule retains its documented cross-version limit. The unchanged UI check script emits Node DEP0190; UI checks exit 0.
