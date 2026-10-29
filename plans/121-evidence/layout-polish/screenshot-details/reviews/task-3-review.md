# Task 3 independent review

Reviewed the task brief, screenshot specification items 14-15, implementation report and full `9fad619e..3010a1e7` diff. Followed the shadcn composition guidance. Read adjacent source only to resolve concrete risks in parameter binding, formatter replacement, mutation linking, dialog lifecycle and the shared radio primitive.

## Verdicts

- **Specification: PASS for the requested behavior, with the bounded-preview quality issue below requiring correction before acceptance.** The toast retains Base Toast and exposes SQL/Review/Deny; the dialog exposes target, authoritative requester/session context and single-choice modes. Original request data and decision payload remain independent of the presentation helper. The parent separately identified minor toast-title/mock fidelity for its cosmetic follow-up; that is not a safety blocker.
- **Quality: CHANGES REQUIRED — one P2 finding.** The current input caps do not bound expanded preview size or synchronous rendering work.

## Finding

### P2 — Bound cumulative replacement output before materializing the inline preview

**Location:** `src/SqlHarness.Dashboard/ui/src/mutations/sqlDisplay.ts:38-43` (input-only guards at line 7); triggered synchronously by `ApprovalDialog.tsx:22`.

The Proxy returns a complete literal for every SQL occurrence, but only the original SQL and the sum of distinct parameter values are capped. Repeating a legal parameter therefore multiplies its value without an output budget. This runs in `useMemo` immediately when Review opens, including while the default mode is As sent. An otherwise valid pending request can allocate tens/hundreds of MB of preview text and stall the operator's approval UI; switching to Values inline adds highlighting/DOM work. The existing guard does not provide the claimed bounded work.

Safe reproduction against the current helper via in-memory TypeScript transpilation:

```ts
const sql = "UPDATE t SET x=@a WHERE " +
  Array(100).fill("@a IS NOT NULL").join(" AND ")
const parameters = [{ name: "a", type: "nvarchar(max)", value: "a".repeat(10_000) }]
inlineMutationSql(sql, "sqlserver", parameters)
```

Observed **1,919 input SQL characters + 10,000 value characters -> 1,012,224 output characters**, no fallback reason (approximately 57 ms in Node). No large allocation experiment was performed. Both inputs are far below current limits, and repeated references are valid binding input.

**Requested fix:** Track cumulative literal characters across Proxy accesses and stop before exceeding a documented preview budget, accounting for SQL/escaping overhead; fall back to exact As sent with the existing explanation. A post-format length check alone still constructs the oversized string. Keep normal repeated-parameter requests working within the budget.

**Regression coverage:** A modest repeated long `nvarchar(max)` case must return exact original SQL plus a reason before expansion exceeds budget; a small repeated text/numeric case must still inline correctly. A dialog regression should confirm the bounded fallback leaves As sent and decision controls usable and the original request unchanged.

## Verified boundaries

- `MutationRequesterByRequestId` joins only `mutation_requests.operation_id -> operations.session_id -> sessions.agent_kind`. Existing `LinkMutation` validates the unique successful prepared mutation, digest, one-time token hash and evidence version. The existing broker invokes `onAccepted` before adding/dispatching pending state. Missing/old/unlinked context stays unknown. No change to these execution or authorization checks is present in the diff.
- Modes do not alter SQL, parameters, digest or API decision payload. The existing 1-second approval delay, expiry checks, Enter guard and error/busy behavior remain; the new submission flags disable decisions while in flight. Toast failures retain the request until authoritative removal.
- Independent bounded probes preserved SQL Server system variables, escaped bracket/double-quoted identifiers, identifier-internal `@`, nested comments, PostgreSQL dollar quotes/escaped strings and operators, negative numeric substitution, and precision-preserving bigint text. Unsupported/ambiguous inputs fall back. The helper's stricter decimal spelling/precision coverage is conservative and does not fabricate a replacement.
- Shared `SegmentedRadioGroup` only forwards a new optional `disabled` property to the existing Base UI Radio primitive. Existing callers without that property retain their existing behavior and interface.
- Independently reran `npx vitest run src/mutations/Approval.test.tsx src/mutations/sqlDisplay.test.ts src/mutations/mutationStore.test.ts`: **PASS, 3 files / 16 tests**. The source report additionally records 35 focused .NET tests, typecheck/lint/build and scoped formatting; those checks were not rerun in this review. These are scoped evidence, not a full verification gate. Parent browser checks and final Windows gate remain outside this task review. No live mutation, DB write, source edit, index change or HEAD change was performed.
