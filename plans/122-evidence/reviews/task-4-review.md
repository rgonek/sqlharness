### Spec Compliance

- ❌ Needs fixes: `src/SqlHarness.Dashboard/Mutations/MutationEndpoints.cs:114` accepts `operationRef` directly from the HTTP request, while `src/SqlHarness.Core/Journal/ActivityJournal.cs:466` validates only that it identifies a unique eligible mutation operation. It does not establish that the operation prepared this request. A caller can supply the reference of an unrelated, previously unlinked mutation operation, including a dry run, and its detail then presents this request's approval and confirmed affected rows as its own. Uniqueness is not provenance. Bind the correlation to a verifiable preparation identity or equivalent exact request association, without relying on SQL/time matching, and cover mismatched/replayed references.
- ⚠️ Cannot verify: live provider batch semantics, disconnected waiter execution, and full Windows gate from the diff and report; the reported multi-statement provider result is simulated and focused test output is not persisted. UI is explicitly outside this task's current scope.

### Strengths

- `src/SqlHarness.Core/Journal/ActivityJournal.cs:466` and `:488` gate relation and result writes on evidence version 1; `src/SqlHarness.Dashboard/JournalReader.cs:1072` gates projection. The migration is additive and the tests cover old and future markers.
- `src/SqlHarness.Dashboard/Mutations/MutationBroker.cs:126` links before publication/execution and skips the callback for joined submissions; `src/SqlHarness.Dashboard/Mutations/MutationExecutor.cs:105` records a provider total only for success and ignores negative totals in the journal.
- Policy and digest do not depend on the new metadata; link failures are best effort and tested.

### Issues

#### Critical (Must Fix)

- None.

#### Important (Should Fix)

- `src/SqlHarness.Dashboard/Mutations/MutationEndpoints.cs:114`, `src/SqlHarness.Core/Journal/ActivityJournal.cs:466`: caller-supplied correlation can attribute one request's lifecycle and affected rows to a different mutation operation. This makes persistent-mutation evidence factually false, even though execution approval itself is unchanged. Add an exact, verifiable association and test different-request and dry-run reference injection/replay.

#### Minor (Nice to Have)

- None.

### Assessment

**Task quality:** Needs fixes

**Reasoning:** Evidence gating and successful-result handling are appropriately bounded, but the linkage trusts an unvalidated reference and can fabricate an operation's approval and execution history.

**Focused check:** inspected operation-reference creation and journal handling (`SqlHarnessModule.cs:141`, `JournalingModule.cs:55`, `ActivityJournal.cs:93`) for the concrete risk that HTTP correlation points to an unrelated operation; no binding is enforced at the changed link query. No tests were rerun.
