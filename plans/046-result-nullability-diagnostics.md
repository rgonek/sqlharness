# Plan 046: Report nullability differences separately from result-data equivalence

> Read current source before editing. Inspect drift since `729d88f` in the scope below and adapt implementation details within this authorized intent. A changed file alone is not a STOP condition. Escalate an incompatible contract or missing required proof. This file plans future implementation; writing it runs no SQL.

## Status

- Status: DONE — merged locally to main fdfc072 from feat/plan-046-nullability (3f425ef..66e70b6: 45bd63a core, 878bcd7 CLI/MCP/journal, 7a4fab1 docs, d4259ab/7500da7/5d9b5de final-review fixes incl. provider-unknown nullability → unavailable, 66e70b6 format); Windows verify.ps1 `verify: OK` (Mcp.Tests 384 passed/4 skipped, SqlHarness.Tests 3678 passed) and Linux verify-linux.ps1 `verify-linux: OK` (388 total/385 passed/3 skipped; 3678 passed) on 66e70b6; git diff --check: 0; snapshots unchanged; no live DB checks; not pushed.
- Priority: P2
- Effort: M
- Risk: MED
- Depends on: none; coordinate 035
- Planned at: `729d88f`, 2026-10-11

## Why this matters

Two queries returning identical rows can report Equivalent=false solely because provider schema metadata differs in allowNull, while row differences remain zero. Nullability is useful contract evidence, but should be reported separately from whether observed results match.

## Current state

- src/SqlHarness.Core/ResultEquivalence.cs:173 checks SchemaHash before comparing rows; :435 writes allowNull into that hash.
- ResultEquivalenceReport exposes one Equivalent field and directional row counts, without a schema-difference reason.
- Ordered, Multiset and Set have different row semantics; none should silently discard type, column ordinal or result-set boundaries.
- Summary, MCP and journal consumers must preserve any new diagnostic; old saved artifacts cannot gain missing evidence retroactively.

## Scope

src/SqlHarness.Core/ResultEquivalence.cs; CanonicalResults.cs only schema representation wiring; Contracts.cs compare operation if adding strict option; SqlHarnessModule.cs/CompareCellRunner.cs/CompareMatrixRunner.cs for propagation only; BenchmarkSummary.cs; AgentOutputProjection.cs; Journal benchmark evidence only if serialized equivalence stored; src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs and compare option builder; src/SqlHarness.Mcp/McpOperationMapper.cs and Tools argument/catalog/handlers; tests/SqlHarness.Tests/ResultEquivalenceTests.cs, compare CLI/core tests; tests/SqlHarness.Mcp.Tests/McpMappingTests.cs, McpToolSchemaTests.cs, McpOutputTests.cs; README.md; docs/mcp.md; AGENTS.md.

No dashboard API, user configuration edits, live production queries, commit, merge, installation or push is authorized by this plan. Match existing xUnit and fake-provider conventions; use synthetic fixtures, not operational SQL/data. Persistent-mutation controls, target locking, timeout and byte limits retain their separate purpose.

## Steps and acceptance

1. Split the comparison schema fingerprint into the existing data-compatible structure (result-set ordinal, column ordinal/name/type) and full metadata including allowNull. Keep row canonicalization unchanged.
2. Default data equivalence ignores only allowNull. Add nullable NullabilityEquivalent diagnostic: true when all measured runs match the reference metadata, false when a difference is observed, null when comparison is off or evidence absent. Never infer true for legacy reports.
3. Add explicit strict nullability comparison: CLI --compare-nullability and MCP compareNullability, default false. Strict mode requires both data equivalence and nullability agreement. Preserve nullability diagnostics in either mode; report a stable reason when strict nullability alone causes mismatch.
4. Carry the additive diagnostic and policy through compare, matrix cells, bounded summary and artifacts; update MCP schema. Do not change SnapshotDiffer or snapshot canonical serialization: they have a distinct persistence contract.
5. Test identical rows with nullable/nonnullable columns across Ordered/Multiset/Set, including empty results; strict mode rejects only the metadata mismatch. Different column types/names/ordinals/result sets still fail. Real row differences remain visible with unchanged directional counts. All measured runs participate.
6. Reconcile 013/035: a default nullability-only difference must not prevent an otherwise valid latency decision. Strict-mode mismatch follows the specified equivalence gate. Keep legacy artifacts readable and unknown metadata diagnostics unavailable.

After each logical implementation change, run the relevant subset of the focused test command below. Build first when needed. All selected tests must pass; a skipped required provider scenario is not proof.

## Verification

- Build: `dotnet build SqlHarness.sln -warnaserror` — exit 0, no warnings/errors.
- Focused: `dotnet test SqlHarness.sln --no-build --filter "FullyQualifiedName~ResultEquivalence|FullyQualifiedName~Compare|FullyQualifiedName~McpMapping|FullyQualifiedName~McpOutput|FullyQualifiedName~McpToolSchema"` — exit 0, at least one selected test, no failed selected tests; confirm the intended regression tests actually ran.
- Final Windows gate: `pwsh -NoProfile -File scripts/verify.ps1` — exit 0 and literal `verify: OK`.
- Final Linux gate: `pwsh -NoProfile -File scripts/verify-linux.ps1` — exit 0 and successful completion of its full gate on ext4.
- `git diff --check` — exit 0.

Run one final gate on each required platform after the final relevant code change. Repeat or broaden verification only for a remaining failure or risk. Record literal results and skipped live checks separately.

## Done criteria

- [ ] Identical observed data with only allowNull differences is equivalent by default and reports the difference.
- [ ] CLI and MCP strict option reject that difference with a clear reason.
- [ ] Other schema and row comparisons preserve their semantics.
- [ ] Snapshots and legacy artifact readability remain compatible.
- [ ] Focused and required platform gates pass, and plans/README.md records evidence.
- [ ] Source, CLI/MCP descriptions and documentation agree on the final behavior.

## Stop conditions and maintenance

Stop if changing row/schema hashes would silently reinterpret stored snapshots or if a provider type mismatch is being relabelled as nullability-only. Coordinate additive schemas with 035 instead of inferring missing historical facts.

Future changes must preserve the specific boundary tested here rather than adding generic hiding or syntax restrictions. Update the plan if implementation evidence changes its premise; historical tests are evidence to examine, not authority to preserve obsolete policy.
