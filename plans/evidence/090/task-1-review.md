### Spec Compliance

- ✅ Spec compliant for the current matcher paths. Full sweeps prune kind-qualified identities against retained operations and the in-flight batch, and a null-title cache row alone is no longer sufficient evidence of live work (`src/SqlHarness.Dashboard/Transcripts/TranscriptMatcher.cs:307`, `src/SqlHarness.Dashboard/Transcripts/TranscriptMatcher.cs:418`). Targeted cycles keep their selected-only behavior (`src/SqlHarness.Dashboard/Transcripts/TranscriptMatcher.cs:319`).
- ✅ Binding global constraints, verbatim: “Transcript reading stays in the dashboard, opt-in, bounded and allowlisted. Use actual operation/staging lifetimes, not conversation-cache existence or a wall-clock timeout, as retention evidence. Windows `just verify` and explicit branch formatting; Linux not required. No live transcript scans, merge or push authorized by this plan.” The opt-in gate remains before matcher I/O (`src/SqlHarness.Dashboard/Transcripts/TranscriptMatcher.cs:84`); the new retention query reads journal identity/title fields, in 400-identity chunks (`src/SqlHarness.Dashboard/Transcripts/TranscriptMatcher.cs:454`), while transcript parsing remains allowlisted (`src/SqlHarness.Dashboard/Transcripts/TranscriptLines.cs:56`).
- ✅ Controller ruling is sound for the current production paths. The `ScanCodex` hunk starts mid-function, so I checked the focused call path: `Run` captures operation identities in `Batch` before scanning (`src/SqlHarness.Dashboard/Transcripts/TranscriptMatcher.cs:97`), and `AddCodexIdentities` adds kind-qualified Codex/OpenCode identities (`src/SqlHarness.Dashboard/Transcripts/TranscriptMatcher.cs:401`). A focused source search found the sole `agent_tool_calls` insert in `TranscriptMatcher`, which writes the literal `claude` kind; the seven-day expiry and retention reference are visible at `src/SqlHarness.Dashboard/Transcripts/TranscriptMatcher.cs:24`, `src/SqlHarness.Dashboard/Transcripts/TranscriptMatcher.cs:500`, and `src/SqlHarness.Dashboard/Transcripts/TranscriptMatcher.cs:524`. The table’s conversation foreign key and retention rule are at `src/SqlHarness.Core/Journal/JournalSchema.cs:282` and `src/SqlHarness.Core/Journal/JournalRetention.cs:211`. No current Codex/OpenCode staged-only row can exist, so the in-flight batch regression covers the applicable boundary (`tests/SqlHarness.Tests/Dashboard/Transcripts/TranscriptMatcherTests.cs:386`).
- ⚠️ Cannot verify from this Task 1 diff: the final Windows `just verify` gate remains Task 2 closure work (`plans/090-matcher-title-backfill-retention.md:55`). The controller reports that explicit branch formatting failed with `FINALNEWLINE` on both changed C# files; Linux verification is not required. No live transcript scan, merge, or push was performed for this review.

### Strengths

- The live set is kind-qualified and based on current operations plus the batch snapshot, so a deleted operation captured in the current sweep remains live for that sweep and is pruned on the next (`src/SqlHarness.Dashboard/Transcripts/TranscriptMatcher.cs:418`, `tests/SqlHarness.Tests/Dashboard/Transcripts/TranscriptMatcherTests.cs:386`).
- Pruning is full-sweep-only; the resumable cursor/restart path remains intact, and the targeted lookup does not consume unrelated pending work (`src/SqlHarness.Dashboard/Transcripts/TranscriptMatcher.cs:306`, `src/SqlHarness.Dashboard/Transcripts/TranscriptMatcher.cs:349`, `src/SqlHarness.Dashboard/Transcripts/TranscriptMatcher.cs:377`).
- The tests exercise orphan eviction/reintroduction, same-ID kind separation, interrupted scans, and repeated cycles; the internal count seam avoids reflection and does not add a public API (`tests/SqlHarness.Tests/Dashboard/Transcripts/TranscriptMatcherTests.cs:301`, `tests/SqlHarness.Tests/Dashboard/Transcripts/TranscriptMatcherTests.cs:328`, `tests/SqlHarness.Tests/Dashboard/Transcripts/TranscriptMatcherTests.cs:359`, `tests/SqlHarness.Tests/Dashboard/Transcripts/TranscriptMatcherTests.cs:424`, `src/SqlHarness.Dashboard/Transcripts/TranscriptMatcher.cs:36`).
- The implementer reports focused matcher tests passing 26/26 with meaningful RED evidence. I did not rerun the reported suite, per the review instructions.

### Issues

#### Critical (Must Fix)

None.

#### Important (Should Fix)

- Plan-mandated branch formatting fails with `FINALNEWLINE` on both changed C# files, per the controller's branch-format gate result. Add the required final newline in `src/SqlHarness.Dashboard/Transcripts/TranscriptMatcher.cs:680` and `tests/SqlHarness.Tests/Dashboard/Transcripts/TranscriptMatcherTests.cs:695`, then rerun the scoped formatter. I did not rerun the gate.

#### Minor (Nice to Have)

- `plans/090-matcher-title-backfill-retention.md:41` names `tests/SqlHarness.Tests/Transcripts/TranscriptMatcherTests.cs`, which does not exist; the implementation correctly changed the existing file under `tests/SqlHarness.Tests/Dashboard/Transcripts/`. Correct the plan path during Task 2 closure.
- The plan still marks the literal staged-only regression as completed (`plans/090-matcher-title-backfill-retention.md:21`, `plans/090-matcher-title-backfill-retention.md:46`, `plans/090-matcher-title-backfill-retention.md:48`), while the tests cover the in-flight batch boundary. The controller’s source-based ruling makes that substitution correct for the current code, but the plan/checklist should describe the conditional staging path so the completed evidence is not overstated.

### Assessment

**Task quality:** Needs fixes

**Reasoning:** The implementation matches the current production ownership model, keeps query work bounded by tracked/current identities, and preserves full-sweep versus targeted-cycle behavior. The required branch-format gate currently fails; after that repair, the remaining findings are nonblocking plan-documentation alignment items. Final Windows verification also remains pending under Task 2.
