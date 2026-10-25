# Plan 116 decision: accept capabilities-only producer identity

**RESEARCH COMPLETE — implementation not authorized by this study.**

## Decision

**Accept candidate A (capabilities only) as the bounded next implementation direction. Defer B (journal-session persistence) and C (artifact-manifest persistence).** This is a design decision, not authorization to implement. A future numbered implementation plan and separate operator selection are required before product changes.

Candidate A adds an always-present, additive `producer` object to `sqlharness_capabilities`, populated from the MCP and Core assemblies already loaded in that running process. It gives a connected caller direct evidence for the current producer build. Its exact contract, unavailable/null behavior, assembly facts, paging reservation, future file map and regression commands are in [design.md](design.md).

## Evidence and scope

The study is tied to source revision `443f468e620e98c2e65fb109040d91ef06c286c5`. The inventory confirms that capabilities currently expose frozen configuration/runtime identity but not the loaded MCP/Core assembly identity. `configurationRef`, MCP protocol/client metadata, artifact ownership and producer-operation linkage answer different questions and remain unchanged.

The seven scenarios in [cases.json](cases.json) and [results.json](results.json) cover release identity; two development builds with equal `0.0.0` versions and different MVIDs; missing optional metadata plus a required-version-unavailable subcase; a legacy artifact without producer provenance; producer versus reader build; loaded facts versus a different PATH/build candidate; and multiple capabilities root pages. The independent [verifier](verify.ps1) checks the availability rules, null fields, identity stability, provenance, excluded path/secret fields, explicit budget fit/non-fit and cursor progress.

The fixture uses PowerShell `ConvertTo-Json` compact UTF-8 byte counts. These are **non-wire estimates** from a compact synthetic capabilities shape. Its root page sizes are predetermined by fixture `pageSize`; they do not test production budget-driven paging, the complete current capabilities envelope, `System.Text.Json`, or the MCP SDK. The observed fixture pages fit the 4,096-byte minimum and 65,536-byte default budgets, but this is not production minimum-budget acceptance. The fixture does not run a real client or MCP process, identify historical binaries, measure retained SQLite/file growth, or establish token savings.

## Why A; why B and C wait

- **A resolves a current-process attribution gap.** A connected caller can compare the exact loaded MCP and Core assembly facts with an inspected source/build. The identity comes from those in-memory assemblies and remains stable across capability pages. It does not tell a later journal/artifact reader which build handled an earlier request after the producer process exits.
- **B could associate new journal sessions with the MCP process that recorded them.** It would require persisted session-model/schema/upsert and dashboard projection changes, migration/old-row semantics, and proof that the dashboard carries stored producer data without substituting its own build. The current evidence does not show repeated demand for that storage cost. Historical 107/113 rows remain unresolved and cannot be backfilled.
- **C could preserve producer identity with a copied artifact after journal loss.** It adds writer-context plumbing, manifest compatibility and old-manifest behavior, with duplicated provenance storage. It must stamp execution-time producer facts and never substitute the reader's build. The current evidence does not establish enough recurrence to justify this broader boundary.

The [inventory](inventory.md) preserves the incident evidence limits: Plan 107's PATH/source discrepancy did not establish the historical process's loaded managed assemblies, and Plan 113's mitigation does not establish the original cause or loaded binary. A cannot repair either historical record. B/C, if later selected, only apply prospectively to new evidence and likewise cannot reconstruct missing historical identity.

## Costs, unresolved questions, and future acceptance

Candidate A adds roughly 300–400 UTF-8 bytes per ordinary synthetic capability response in the observed compact projection and no retained storage. Actual complete-envelope cost and minimum-budget progress remain unknown until the implementation is exercised through the production adapter. B was estimated at 250–400 bytes per new session; C at 250–400 bytes per manifest, in addition to schema, compatibility, plumbing and dashboard/reader costs. These JSON estimates are not database/file growth measurements.

Before implementation is accepted, resolve the production details through tests: availability when a required loaded assembly/version is unreadable; optional metadata absence independently yielding `partial`; stable full producer reservation under the real response budget; the existing explicit `output_budget_too_small` outcome when the reservation cannot fit; root omission/cursor progress; and no handshake-version change. Actual SDK envelope sizing and client acceptance are required future checks. The future regression commands and full Windows verification/formatting gate are listed in [design.md](design.md); none was run by this study.

The study did not perform process inventory, user-home investigation, database access, server/client launch or restart, source/test edits, a product gate, merge or push. No implementation plan was created.

## Disposition

- **A: ACCEPT as the next implementation direction**, subject to a separately numbered plan and operator selection.
- **B: DEFER** pending recurring evidence for durable session-level attribution and explicit migration/dashboard scope.
- **C: DEFER** pending recurring evidence for artifacts that need provenance after journal loss and explicit manifest/writer compatibility scope.

Research is complete. Product behavior is unchanged; Plans 107/113 historical loaded identity remains unresolved.
