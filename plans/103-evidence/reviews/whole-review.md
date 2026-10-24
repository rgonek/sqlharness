# Plan 103 whole-branch review

Reviewed base `6d543e50` through head `92cacf37`, against the plan Contract/tasks, repository contract, verification policy, staged evidence, production diff and relevant regression/SDK/golden changes. Read-only review; no tests rerun. Final Windows gate and explicit branch formatting remain controller-owned and pending.

## Strengths

- The storage projection reuses journal extraction and counter reconciliation rather than inventing a competing completeness test. Counter attribution and hidden-function limitations remain separate, so reconciled object IO does not hide UDF/TVF uncertainty.
- Statement rows now retain variant, set, matrix cell, repetition and statement identities. Artifact reads remain offline and scoped through the existing manifest/owner path; no new SQL, credential or mutation surface is introduced.
- Versioned additive evidence models preserve legacy reading with unavailable attribution. PostgreSQL absence remains unavailable rather than measured zero. Missing entire runs/cases receive explicit compact limitations.
- Hidden-list source omissions and Unicode name clipping have explicit metadata. Existing source windows, continuation mechanics and total byte ceilings remain in place. SDK fixtures exercise the compact-to-saved-artifact workflow at 4096 and 65536 for the ordinary fixture.
- Documentation correctly limits complete to collected-counter coverage. The Plan 109 historical baseline remains separate from updated current-contract goldens.

## Critical

None found.

## Important

1. **A valid hidden-work row can become unreadable at the supported minimum budget.** `src/SqlHarness.Core/AgentOutputProjection.cs:576-600`, together with `src/SqlHarness.Core/StatementMetrics.cs:724-729`.

   Persistence keeps up to 16 hidden-work entries with names up to 512 UTF-8 bytes each. Statement response projection only reduces/clips `TopOperators`; it never trims `HiddenWork` or its names. Sixteen distinct long TVF names therefore exceed 8192 name bytes alone, before JSON escaping and row/envelope fields. At `maxResultBytes=4096`, every candidate detail limit fails to fit the first row and throws `SqlHarnessSafetyException`; reducing pageSize cannot help because this is one statement. The agent cannot retrieve that affected statement at the minimum budget promised by Task 3. Escaped multibyte names make the gap larger. Current long-name and 18-function tests check persistence separately and do not combine these conditions with MCP minimum-budget projection.

   Fix by budgeting hidden-work detail within a statement response, retaining exact row selectors, attribution and limitations, and adding response-omitted items to `HiddenWorkOmitted` (plus truthful name truncation if names shrink). Add a regression with multiple distinct long escaped/multibyte TVF names through the real MCP response at 4096 and default budgets; require a returned row with correct identity/limitations and explicit omissions. Do not increase the total ceiling.

## Minor

1. **Deferred issue confirmed: a global omission downgrades unaffected cases.** `src/SqlHarness.Core/AgentBenchmarkOutput.cs:388`.

   Any `section.OmittedStatements > 0` marks every case partial, including an earlier case whose complete rows were all retained. It can also label an entirely unavailable case partial. This is conservative rather than a false-complete claim, so I retain Minor severity; it is not the merge blocker above. It does fall short of the stated per-case scoping contract. Recommended follow-up: retain omission counts keyed by variant/set/cell during projection and downgrade only affected cases. Cover one fully retained complete case, one truncated case and one wholly unavailable case. Explicit acceptance should record this limitation if deferred again.

## Declined to judge

- Final Windows gate, branch formatter and integrated-tree status: pending controller work, not asserted by this review.
- Live database/installed MCP binary behavior, Linux acceptance, model-token savings and complete SQL execution cost: outside this offline branch review and not established by synthetic fixtures.

## Ready to merge

**With fixes.** Address the Important minimum-budget retrieval gap and obtain scoped verification/re-review. The Minor may be explicitly accepted/deferred. Independently, the controller must finish the required Windows gate and branch formatting before declaring implementation complete.
