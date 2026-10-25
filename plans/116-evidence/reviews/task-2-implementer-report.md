# Plan 116 Task 2 report: loaded producer identity study

## Status

Task 2 deliverables are complete at study revision `443f468e620e98c2e65fb109040d91ef06c286c5`. The study remains offline and synthetic. Product source and tests were not changed; no process, credentials, client, server, or database was inspected or launched. Persistence options B/C are explicitly deferred in `plans/116-evidence/design.md` for Task 3 disposition. This is not implementation, SDK wire acceptance, or historical attribution of Plans 107/113.

## Contract and scenario results

The proposal adds `producer.identityVersion`, `availability`, `reason`, and loaded MCP/Core assembly facts to the existing capability root. Numeric assembly versions are the loaded `AssemblyName.Version` rendered as a dotted string; informational version and MVID remain nullable. `McpHost.ServerVersion` and MCP `serverInfo.version` remain unchanged. Optional missing facts are represented as explicit null plus `partial`; older evidence is unavailable and is never stamped by a newer reader. MVID is diagnostic only.

The A/B/C study compares capabilities-only, new-session persistence, and new-manifest persistence. Task 2 freezes A as the immediate candidate and defers journal/artifact persistence pending the Task 3 decision. The proposed root reservation keeps one complete producer object on every capabilities page and prioritizes that object plus the minimum envelope before paging optional roots. The verifier checks that synthetic root pages fit 4,096 bytes, advance their cursor, and keep the same producer identity.

All seven required scenarios are represented. The development scenario contains two distinct loaded record pairs with equal `0.0.0` versions and distinct MCP/Core MVIDs. The optional-metadata scenario also contains a required-version-missing subcase: that subcase derives `unavailable` with a null version. The PATH candidate, including its absolute path, stays fixture-only and does not enter candidate output. The reader-build scenario keeps the persisted producer facts distinct from the reader's current capabilities facts.

`evaluate.ps1` uses PowerShell `ConvertTo-Json` compact serialization. All reported UTF-8 byte counts are **non-wire estimates**, not `System.Text.Json` or MCP SDK measurements. Capability page estimates were: release 586 B; development 580 B; optional metadata unavailable 522 B; old artifact 586 B; reader differs 586 B; PATH differs 586 B; paged roots 631 B and 612 B. Every page fit both 4,096 and 65,536 bytes. Candidate journal producer JSON estimates were 305–369 B. An artifact payload size is emitted only when persisted producer data exists: the reader-differs artifact producer measured 373 B; the old artifact has no producer and reports no payload size. These are JSON estimates, not SQLite row/file growth measurements. `results.json` preserves exact values and explicit fit/non-fit flags.

## Exact focused checks

Command: `pwsh -NoProfile -File plans/116-evidence/evaluate.ps1`

Exit: `0`

Output:

```text
Wrote D:\Dev\sqlharness\.worktrees\plan-116\plans\116-evidence\results.json (7 scenarios; non-wire UTF-8 estimates).
```

Command: `pwsh -NoProfile -File plans/116-evidence/verify.ps1`

Exit: `0`

Output:

```text
PASS: seven independent synthetic scenarios; availability, identity stability, provenance, path exclusion, byte-budget declarations, and progress-capable root paging.
```

`verify.ps1` independently derives availability/reason from each loaded record pair, checks those derived states against separate golden expectations and projected output, and checks required-version-unavailable behavior. It also checks exact case IDs, producer fields/nulls, identity preservation across pages, distinct development MVIDs, legacy artifact unavailability, producer-versus-reader distinction, path/secret field exclusion, artifact-size attribution, byte estimates and budget declarations, and root paging progress at the 4,096-byte minimum. Both scripts throw on missing inputs; evaluator rejects wrong revision/count/case IDs and verifier throws on assertion failure, yielding nonzero PowerShell exits.

The scoped `git diff --check` command produced no output and exit `0`; because evidence files are untracked in this study checkout, this command does not itself inspect their contents. The scripts were rerun after final edits with the exact commands above. No `just verify`, test project, production build, client acceptance, or real MCP wire test was run or required for this notes/fixture-only task.

## Source and design boundary

The reviewed current paths include `McpHost` handshake identity, `McpOperationMapper.BuildCapabilities` and `WithInputRootPage`, MCP process session identity construction, Core `SessionIdentity` / `ActivityJournal.UpsertSession`, dashboard session projection, and Core `ArtifactManifest.ForReport` / writer behavior. CLI Doctor and Core CLI capabilities read their own loaded assemblies; they are not evidence for an MCP server's loaded MCP assembly. Dashboard `JournalReader` is a consumer and must not replace stored producer facts with its own build. An artifact writer must receive facts from the producer's execution context; `ArtifactReader` must not stamp them.

Initial verifier iterations exposed two fixture-check defects (null conversion in the projector and a broad substring check matching `SqlHarness`); both were corrected before the first successful run. The scoped review then found and this fix round corrected circular availability expectations, the missing required-metadata-unavailable case, the inconsistent unavailable-state reservation wording, and artifact-size attribution to the current reader. Residual limitation: the root-page fixture is a compact synthetic capabilities representation with predetermined page sizes; it demonstrates stable identity and cursor progress, not budget-driven page selection. Byte results also do not model every current optional capability field or the actual MCP envelope. Real adapter/System.Text.Json budget behavior and added-object cost in a full production page remain future implementation acceptance work. Historical loaded identity for incidents 107/113 remains unresolved.

## Fix round 1 focused recheck

Command: `pwsh -NoProfile -File plans/116-evidence/evaluate.ps1`

Exit: `0`

Output:

```text
Wrote D:\Dev\sqlharness\.worktrees\plan-116\plans\116-evidence\results.json (7 scenarios; non-wire UTF-8 estimates).
```

Command: `pwsh -NoProfile -File plans/116-evidence/verify.ps1`

Exit: `0`

Output:

```text
PASS: seven independent synthetic scenarios; availability, identity stability, provenance, path exclusion, byte-budget declarations, and progress-capable root paging.
```

`git diff --check` exited `0` with no output. The required-version-missing case is a subcase within the existing `optional-metadata-unavailable` scenario, so the named scenario count remains seven. Artifact producer size now measures the persisted producer only; when no producer was stored, the recorded artifact payload size is null.

## Fix round 2 focused recheck

The required-version-unavailable projection now clears `informationalVersion` and `moduleVersionId` on that assembly entry as well as keeping `version` null. The verifier independently checks all three fields. Artifact provenance distinguishes an omitted `artifactProducer` fixture field (`not_applicable`) from an explicit null producer (`unavailable`, `producer_not_recorded`).

Command: `pwsh -NoProfile -File plans/116-evidence/evaluate.ps1`

Exit: `0`

Output:

```text
Wrote D:\Dev\sqlharness\.worktrees\plan-116\plans\116-evidence\results.json (7 scenarios; non-wire UTF-8 estimates).
```

Command: `pwsh -NoProfile -File plans/116-evidence/verify.ps1`

Exit: `0`

Output:

```text
PASS: seven independent synthetic scenarios; availability, identity stability, provenance, path exclusion, byte-budget declarations, and progress-capable root paging.
```

## Files

- `plans/116-evidence/design.md`
- `plans/116-evidence/cases.json`
- `plans/116-evidence/evaluate.ps1`
- `plans/116-evidence/verify.ps1`
- `plans/116-evidence/results.json`
- `.superpowers/sdd/116-loaded-producer-identity-study/task-2-report.md`
