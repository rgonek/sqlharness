# Task 3 report: mutation toast and review context

Implemented screenshot items 14-15 in the existing isolated worktree. The Base Toast now presents plain environment text, a bounded 240-character/two-line SQL snippet, Review and Deny. Deny uses postMutationDecision; submitted/busy/error/expired state stays visible until authoritative removal. Dialog target sits below the title, requester shows an actual session link when available, and the existing segmented radio component selects Values inline / As sent. Existing parameter table, digest, classification, writer alerts, 1-second approval delay, expiry and Enter guard remain. Both dialog decisions disable during submission.

## Provenance

Only the outbound dashboard mutation SSE snapshot/added projection is enriched. JournalReader joins mutation_requests.operation_id -> operations.session_id -> sessions.agent_kind. The operation_id is populated by existing ActivityJournal.LinkMutation after its exact prepared-operation/digest/token checks; neither submitted session keys nor latest/global agent records are used. Unknown/unlinked requests return null requester. UI renders the retained agent kind through existing agentLabel(). No Core/broker execution, policy, authorization, digest, migration, collection or mutation behavior changed.

## Preview contract and limitations

sqlDisplay.ts uses installed sql-formatter dialect tokenization and strict parameter lookups. It normalizes engine case and one optional parameter-name @ prefix (as Core SqlParameterNames does), detects duplicate/missing names, and leaves quoted strings, identifiers, comments (including nested blocks), SQL Server @@ variables, PostgreSQL dollar quotes and operators intact. Known text/null/integer/decimal/bit values render without JavaScript numeric conversion; numeric-looking default strings stay quoted text. Unsupported types, parse failures, missing/ambiguous names, PostgreSQL backslash text, decimal inputs over 28 digits and inputs above 100,000 SQL/value characters or 500 parameters return exact original SQL with an honest explanation and disabled inline option. As sent remains the default and preserves formatting/copy controls. The immutable original request/parameters/digest never enter a submit payload through this helper.

The preview is presentation rather than executable replacement SQL; not every accepted provider type is previewed. It does not emulate server-dependent encoding, collation or assignment coercions. Browser visual acceptance belongs to the parent fixture harness; no real approve/deny was issued.

## Focused verification

- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~MutationEndpointsTests|FullyQualifiedName~MutationEvidenceTests|FullyQualifiedName~Mutation_requester" --no-restore -p:SkipDashboardUi=true --verbosity quiet`: PASS, 35 tests. Includes verified-link behavior and a projection regression proving unlinked context remains unknown and linked context wins over a different/latest session.
- `npx vitest run src/mutations/Approval.test.tsx src/mutations/sqlDisplay.test.ts src/mutations/mutationStore.test.ts`: PASS, 3 files / 16 tests. Fake fetch/SSE only; lexical boundaries, precision/text/null, case/prefix normalization, fallback, bounded work, requester known/unknown, immutable display modes, toast failed/busy/submitting/expiry, existing lifecycle/delay/Enter tests.
- `npm run typecheck`: PASS.
- `npm run lint`: PASS.
- `npm run build`: PASS. Existing chunk-size advisory remains.
- `dotnet format SqlHarness.sln --no-restore --include src/SqlHarness.Dashboard/JournalReader.cs src/SqlHarness.Dashboard/DashboardModels.cs src/SqlHarness.Dashboard/Mutations/MutationEndpoints.cs tests/SqlHarness.Tests/Dashboard/JournalReaderTests.cs --verbosity quiet`: PASS.
- `git diff --check`: PASS for owned changes (unrelated parent plan has CRLF advisory only).

Earlier local check attempts exposed route parameter naming, seed-test API mismatches, an alert selector and a stub call-list assertion; these were corrected before the passing checks. Initial .NET invocation without SkipDashboardUi failed in its UI build stage; focused .NET checks then used the explicit property and the standalone final UI build passed. No full gate, Linux gate, live DB, publication, merge, push, restart or dependency install performed.

## Self-review

Spec: both screenshot behaviors implemented, no fabricated requester, no automatic decisions, original execution contract untouched. Quality: small pure renderer, reuse existing shadcn/Base UI controls and API path, additive dashboard-only projection, strict fallback and bounded work, focused behavioral tests. Shared SegmentedRadioGroup change only adds optional disabled per option; existing callers are compatible. Shared Stats reader/model fields are preserved. Parent retains plan/index updates, independent review, visual captures and final Windows gate.

Commit: see task completion message (report committed with owned source files).

## Review fix round 1 (2026-10-29)

P2 cumulative preview expansion finding fixed in `a44b2661` (base `3010a1e7`). The strict formatter parameter Proxy now starts its budget at the original SQL length and charges every fully escaped literal occurrence before returning that literal. At more than 100,000 cumulative characters it throws into the existing exact-original As sent fallback, so repeated long parameters cannot first materialize a large expanded result. Counting the SQL placeholders as well as their replacement literals is intentionally conservative. A secondary final-length check rejects formatting-whitespace overhead beyond the same budget; this is additional protection rather than the expansion limiter.

The new long-text repetition regression first failed against the previous implementation, reproducing expansion. After the fix, the helper returns original SQL plus its explanation; small repeated text and precision-preserving bigint still inline. The dialog regression checks disabled inline mode, selected As sent, exact original SQL, usable Deny/Approve after the original delay and immutable request data. Its first run used the native-disabled matcher against Base UI's aria-disabled radio; the assertion was corrected to the component's actual accessibility contract.

Fresh checks on the fix:

- `npx vitest run src/mutations/Approval.test.tsx src/mutations/sqlDisplay.test.ts src/mutations/mutationStore.test.ts`: PASS, 3 files / 18 tests.
- `npm run typecheck`: PASS.
- `npm run lint`: PASS.
- `npm run build`: PASS (existing chunk-size and plugin timing advisories only); final dist ready for parent fake-browser verification.
- `git diff --check`: PASS for owned changes; parent plan CRLF advisory unchanged.

Self-review: only the preview budget and requested regression coverage changed. Original SQL/parameters/digest and lifecycle/decision paths remain unchanged. No C# tests repeated, no full gate, live decision, dependency install, source-target expansion, publication or cosmetic follow-up performed. Exact source fix commit is recorded above; this report-only commit follows it.
