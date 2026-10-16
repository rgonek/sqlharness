# Plan 073: Mutation command and dashboard approval broker

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Drift check (run first):** Plan 072 must be DONE. Run `git diff --stat 488598d..HEAD -- src/SqlHarness.Core src/SqlHarness.Dashboard src/SqlHarness.Mcp src/SqlHarness.Cli README.md docs/mcp.md skills/sqlharness`.
> - Plans 068, 069 and 070 take journal schema versions. This plan uses **the next free journal version**; call it `VersionN` everywhere, including the tests. That is adaptation, not a STOP.
> - Line numbers are from `488598d`.

**Goal:** Agents request persistent changes through a dedicated `mutation` command (CLI) or tool `sqlharness_mutation` (MCP). The local dashboard decides by the profile's mode, or asks the operator in the browser. It then executes the frozen batch with the profile's writer credential. `query --allow-mutation` is removed. This plan supports click approval only; WebAuthn enrollment and verification are deferred to plan 078.

**Architecture:**
- **Core** holds:
  - the policy (`MutationPolicy`: mode × tier → decision);
  - the frozen request and its SHA-256 digest (`MutationRequest`);
  - `SqlHarnessMutationOperation`. It executes only with an `ApprovedMutation` token whose constructor is `internal`. Product internals are available to `SqlHarness.Dashboard` and the existing test assemblies; the CLI and MCP cannot build the token.
- **Dashboard** hosts:
  - `MutationBroker`, an in-memory state machine with a fake-able clock;
  - `MutationExecutor`, which runs through Core with the writer `AuthSpec`;
  - HTTP endpoints: a long-poll submit authenticated by the dashboard token, SSE events, approve/deny as guarded writes;
  - the click-only approval UI, including an in-page notification ping and sound settings.
- **CLI and MCP** share `HttpMutationBrokerClient`, defined in the Dashboard project. The CLI composition root wires it for both `mutation` and `mcp serve`.

**Tech Stack:** .NET 10, C#, ASP.NET Core minimal APIs, Microsoft.Data.Sqlite, ModelContextProtocol, Spectre.Console.Cli, xUnit; React 19, TypeScript, TanStack Query/Router, Vitest, Testing Library.

**Spec:** [2026-10-14-mutation-approval-design.md](../docs/superpowers/specs/2026-10-14-mutation-approval-design.md). [Plan 072](072-mutation-risk-tiers.md) provides the tiers.

## Status

- DONE on feature branch `plan-073` at product revision `2085f0ff` on 2026-10-16. Luna high implementation, Luna xhigh task reviews, Sol low whole-branch review and scoped re-review approved; all five final findings are addressed. Windows `just verify` passed; Linux was explicitly waived and remains unverified. Integrated locally into `main` at `bb2fa5d`; post-merge Windows verification passed. Push remains pending. WebAuthn is deferred to plan 078.
- Supersedes [plan 08 — Windows approval tray](../docs/superpowers/plans/2026-09-26-audit-08-windows-approval-tray.md); the tray stays deferred.
- Priority P1. Effort L. Risk HIGH: the first persistent-write path through MCP, a new dashboard write surface, and a journal migration.

## Global Constraints

**Threat model (spec §1).** The feature protects against agent accidents. It is not isolation from a same-user adversary. README must say so in those words.

**Modes.**
- Modes: `never` (default), `operator`, `yolo`.
- `yolo` auto-executes only the `dml` tier. `schema`, `privilege` and `opaque` always ask.
- `yolo` on a profile whose `environment` is `prod`, `production` or `staging` (case-insensitive) needs `mutations.acknowledgeProductionYolo: true`; without it the profile fails to load. While such a profile exists, the dashboard shows a persistent warning banner naming it, and history marks its unasked executions.
- `operator` and `yolo` require `mutations.writer`, either `{ "sqlUser", "passwordEnvVar" }` or `{ "sameAsReader": true }`.
- Verification is click-only in this plan. `click` is the default. Reject `webauthn` and every unknown verification value with a visible validation/configuration error; never silently convert an unsupported request to click. WebAuthn enrollment, assertion, credential storage, and recovery belong to plan 078.
- The broker reads `targets.json` and `config.json` on every submit, never once at start.

**Classification and limits.**
- The `session` tier never needs approval. `mutation` rejects `read-only` and `session` batches with `not_a_mutation`.
- `privilege` is rejected when any grantee equals the reader principal, compared with `OrdinalIgnoreCase`. The broker resolves the principal at execution time with the reader credential: `SELECT SUSER_SNAME(), USER_NAME()` on SQL Server, `SELECT current_user, session_user` on PostgreSQL.
- Approval TTL: default 120 s, `config.json` `mutations.approvalTimeoutSeconds` 30..600.
- At most one pending request per caller session and one executing request per profile; otherwise `busy`.
- An identical digest while pending joins the existing request, with no second notification.
- An identical digest within 60 s after `denied_by_operator` returns the same denial.

**Wire codes.** Exactly these strings in `status`:
- `success`
- `mutations_disabled`
- `approval_unavailable`
- `approval_timeout`
- `denied_by_operator`
- `busy`
- `not_a_mutation`
- `database_mismatch`
- `principal_protected`
- `failed`
- `outcome_unknown`

**Exit codes.**
- `2`: rejections and approval outcomes;
- `4`: `database_mismatch`;
- `5`: `failed` and `outcome_unknown`;
- `0`: `success`.

**Execution guarantees.**
- One atomic transition per request arbitrates a click that races the deadline. A click after expiry runs nothing.
- A request never executes twice. Consumption happens before the first byte of SQL is sent.

**Sensitive data.**
- Notifications (in-page toast title and browser Notification) never contain SQL, names or values.
- The approval view renders SQL and values as text only, never HTML.
- Callers never receive writer credentials.

**Surface limits.**
- MCP: `McpLimits.MaxTools` becomes 9. The catalog stays within its 32768-byte budget.
- Parity: `mutation` exists in CLI and MCP with the same arguments.

**Process rules.**
- Do not auto-format files; touch only lines the change needs (operator rule).
- Do not touch the operator's own dashboard or API during development; use isolated homes (`SQLHARNESS_HOME` fixtures) in tests (AGENTS.md › Data).
- Completion requires Windows `just verify` on the final product tree. The operator waived `just verify-linux` on 2026-10-16; record Linux as unverified.

## Review Focus

1. The operator clicks Approve at the same instant the TTL ends. Exactly one of `approve` or `expire` wins. If expire wins, nothing executes and the caller sees `approval_timeout`. Test in T4.
2. The agent kills the CLI (or the MCP client cancels) while the request is pending. The request becomes `cancelled`, the toast disappears, and a later click does nothing. Test in T4/T6.
3. The connection drops after the batch was sent. The status is `outcome_unknown` (exit 5), never `failed` with "nothing ran", and the broker does not retry. Test in T5.
4. `targets.json` changes the profile from `operator` to `never` while a request is pending. Approve then re-checks the mode at execution start and returns `mutations_disabled`. Test in T5.
5. A batch with `<script>` or `"><img onerror=…>` in a string literal shows literally in the approval view and in the history. Test in T10.

## File map

| File | Responsibility |
|---|---|
| `src/SqlHarness.Core/Targets/TargetProfile.cs`, `ProfileStore.cs` | `Environment`, `Mutations` profile fields and validation |
| `src/SqlHarness.Core/SqlHarnessConfig.cs` | `MutationsConfig` (`DefaultMode`, `Verification`, `ApprovalTimeoutSeconds`) |
| `src/SqlHarness.Core/Mutations/MutationPolicy.cs` (new) | modes, policy decision, wire codes |
| `src/SqlHarness.Core/Mutations/MutationRequest.cs` (new) | frozen request and canonical digest |
| `src/SqlHarness.Core/Mutations/ApprovedMutation.cs` (new) | internal-constructor approval token |
| `src/SqlHarness.Core/Contracts.cs`, `SqlHarnessModule.cs` | `SqlHarnessMutationOperation`, dry run, approved execution |
| `src/SqlHarness.Core/Mutations/IMutationBrokerClient.cs` (new) | submit contract used by CLI and MCP |
| `src/SqlHarness.Core/Journal/JournalSchema.cs`, `ActivityJournal.cs` | `mutation_requests` table (`VersionN`) |
| `src/SqlHarness.Dashboard/Mutations/MutationBroker.cs` (new) | state machine, limits, dedupe, cooldown |
| `src/SqlHarness.Dashboard/Mutations/MutationExecutor.cs` (new) | principal check, writer execution, outcome, journal |
| `src/SqlHarness.Dashboard/Mutations/MutationEndpoints.cs` (new) | submit long-poll, SSE events, approve/deny, history |
| `src/SqlHarness.Dashboard/Mutations/HttpMutationBrokerClient.cs` (new) | client: discovery, autostart, long-poll |
| `src/SqlHarness.Dashboard/DashboardSecurity.cs`, `DashboardServer.cs` | routes and guards |
| `src/SqlHarness.Cli/Commands/MutationCommand.cs` (new), `SqlHarnessCommands.cs`, `SqlHarnessCli.cs` | CLI command; remove `--allow-mutation`/`--confirm-database` |
| `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`, `McpOperationMapper.cs`, `McpLimits.cs` | `sqlharness_mutation` |
| `src/SqlHarness.Dashboard/ui/src/mutations/*` (new), `pages/MutationsPage.tsx`, `pages/SettingsPage.tsx`, `components/AppLayout.tsx` | toast and sound ping, approval dialog, history, settings |
| `scripts/hooks/protect_sqlharness_config.py` + test | broader agent-side hygiene |
| `README.md`, `docs/mcp.md`, `skills/sqlharness/SKILL.md`, `AGENTS.md`, `Capabilities.cs` | contract docs |

---

### Task 1: Profile and config fields

**Files:**
- Modify: `src/SqlHarness.Core/Targets/TargetProfile.cs`, `src/SqlHarness.Core/Targets/ProfileStore.cs:16-70`, `src/SqlHarness.Core/SqlHarnessConfig.cs:1-62` and its loader validation, `src/SqlHarness.Dashboard/DashboardSettings.cs` (expose and validate the new section), `src/SqlHarness.Dashboard/DashboardProfiles.cs` (show mode, environment, writer kind and `acknowledgeProductionYolo`, never env var values)
- Test: `tests/SqlHarness.Tests/Targets/ProfileStoreTests.cs` (or the existing profile test file in `tests/SqlHarness.Tests/Targets`), `tests/SqlHarness.Tests/SqlHarnessConfigTests.cs` (or wherever `SqlHarnessConfigLoader` is tested: `grep -rln SqlHarnessConfigLoader tests`)

**Interfaces:**
- Produces:

```csharp
// Core/Mutations/MutationPolicy.cs (types only in this task)
public enum MutationMode { Never, Operator, Yolo }
public enum MutationVerification { Click }

// Targets
public sealed record MutationWriter(string? SqlUser = null, string? PasswordEnvVar = null, bool SameAsReader = false);
public sealed record ProfileMutations(string Mode, MutationWriter? Writer = null, bool AcknowledgeProductionYolo = false);
// TargetProfile gains trailing optional parameters:
//   string? Environment = null, ProfileMutations? Mutations = null

// Config
public sealed record MutationsConfig
{
    public string DefaultMode { get; init; } = "never";
    public string Verification { get; init; } = "click";
    public int ApprovalTimeoutSeconds { get; init; } = 120;
}
// SqlHarnessConfig gains: public MutationsConfig Mutations { get; init; } = new();
```

Validation, inside the existing `ProfileStore.Load` loop:
- `mutations.mode` parses case-insensitively to `MutationMode`; otherwise `JsonException("A profile mutation mode is invalid.")`.
- A mode other than `Never` without a writer → `JsonException("A profile that allows mutations needs mutations.writer.")`.
- A writer with `SameAsReader` and `SqlUser`/`PasswordEnvVar` together, or neither → `JsonException("mutations.writer needs sqlUser and passwordEnvVar, or sameAsReader.")`.
- `Yolo` with `Environment` in {`prod`, `production`, `staging`} (OrdinalIgnoreCase) and `AcknowledgeProductionYolo` false → `JsonException("yolo on a prod or staging profile needs mutations.acknowledgeProductionYolo: true.")`.

`SqlHarnessConfigLoader` keeps its strict, fail-closed style: unknown fields or out-of-range values yield `Default` with a warning. `ApprovalTimeoutSeconds` must be 30..600 and modes must be known names. The only supported verification value is `click`; an unsupported value is rejected with an explicit warning/error and is never normalized to click. Settings writes containing unsupported values return a validation error and preserve the existing value.

- [x] **Step 1: Write the failing tests**
  - A profile with `"mutations": {"mode":"yolo","writer":{"sameAsReader":true}}, "environment":"Prod"` fails to load with the acknowledgement message.
  - The same profile plus `"acknowledgeProductionYolo": true` loads.
  - `acknowledgeProductionYolo: true` on a non-yolo profile loads (it is inert).
  - `"mode":"operator"` without a writer fails.
  - A writer with both forms fails.
  - A valid dev profile loads with `Mutations.Mode == "yolo"`.
  - A profile without `mutations` loads as before.
  - `config.json` with `"mutations":{"approvalTimeoutSeconds":10}` yields `Default` plus a warning.
  - `"verification":"webauthn"` is rejected with a visible warning/error and is never treated as an explicit click selection.
- [x] **Step 2: Run them to verify they fail** (`dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~ProfileStore|FullyQualifiedName~SqlHarnessConfig"`).
- [x] **Step 3: Implement** the records, validation, `DashboardSettings` read/write of `mutations` (same autosave rules as other settings) and the `DashboardProfiles` columns.
- [x] **Step 4: Run them to verify they pass**, plus `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~Dashboard`.
- [x] **Step 5: Commit**: `git commit -m "Add per-profile mutation modes, writer credentials and mutation settings"`.

### Task 2: Policy, frozen request, approved execution in Core

**Files:**
- Create: `src/SqlHarness.Core/Mutations/MutationPolicy.cs`, `MutationRequest.cs`, `ApprovedMutation.cs`, `IMutationBrokerClient.cs`
- Modify: `src/SqlHarness.Core/Contracts.cs` (new operation + result), `src/SqlHarness.Core/SqlHarnessModule.cs` (dispatch at the `SqlHarnessQueryOperation` arm near 170-205; PostgreSQL `ApprovedMutation` mode at 199)
- Test: `tests/SqlHarness.Tests/Mutations/MutationPolicyTests.cs`, `MutationRequestTests.cs`, `MutationOperationTests.cs`

**Interfaces:**
- Consumes: plan 072 `SqlUsage.Mutation`, `SqlRiskTier`, `SqlSafetyDecision.Grantees`; Task 1 types.
- Produces:

```csharp
public static class MutationStatus
{
    public const string Success = "success", Disabled = "mutations_disabled", Unavailable = "approval_unavailable",
        Timeout = "approval_timeout", Denied = "denied_by_operator", Busy = "busy", NotAMutation = "not_a_mutation",
        DatabaseMismatch = "database_mismatch", PrincipalProtected = "principal_protected", Failed = "failed",
        OutcomeUnknown = "outcome_unknown";
}

public enum MutationDecisionKind { Reject, Execute, AskOperator }

internal static class MutationPolicy
{
    // tier is never ReadOnly/Session here: the caller rejects those as not_a_mutation first.
    internal static (MutationDecisionKind Kind, string? Status) Decide(MutationMode mode, SqlRiskTier tier) => mode switch
    {
        MutationMode.Never => (MutationDecisionKind.Reject, MutationStatus.Disabled),
        MutationMode.Yolo when tier == SqlRiskTier.Dml => (MutationDecisionKind.Execute, null),
        _ => (MutationDecisionKind.AskOperator, null),
    };
}

public sealed record MutationRequest(
    string Profile, IReadOnlyDictionary<string, string> Vars, string Engine, string Server, string Database,
    string Sql, IReadOnlyList<SqlHarnessParameterInput> Parameters, int TimeoutSeconds, int MaxRows,
    string Tier, IReadOnlyList<string> Grantees, string SessionKey)
{
    /// <summary>SHA-256 over a canonical JSON encoding (ordinal-sorted vars, parameters in order, exact SQL bytes). Lowercase hex.</summary>
    public string Digest { get; }   // computed in the constructor body
}

/// <summary>Proof that the broker consumed an approval for exactly this digest. Only SqlHarness.Dashboard can construct it.</summary>
public sealed class ApprovedMutation
{
    internal ApprovedMutation(string digest, AuthSpec writer) { Digest = digest; Writer = writer; }
    public string Digest { get; }
    internal AuthSpec Writer { get; }
}

public sealed record SqlHarnessMutationOperation(
    SqlTargetRequest Target, string Sql, IReadOnlyList<SqlHarnessParameterInput> Parameters,
    int TimeoutSeconds, int MaxRows, string? ExpectDatabase) : SqlHarnessOperation
{
    public bool DryRun { get; init; }
    public string SessionKey { get; init; } = "";
    internal ApprovedMutation? Approval { get; init; }
}

public interface IMutationBrokerClient
{
    /// <summary>Submits and waits for the terminal result; progress gets a short human status about every 5 s.</summary>
    Task<MutationBrokerResult> SubmitAsync(MutationRequest request, IProgress<string>? progress, CancellationToken ct);
}

public sealed record MutationBrokerResult(string Status, string? Message, SqlHarnessQueryResult? Result);

/// <summary>Module result for an unapproved, non-dry-run mutation: the frozen request the caller submits.</summary>
public sealed record SqlHarnessMutationPrepared(MutationRequest Request);
```

`SqlHarnessQueryReport` is the current result type returned by `SqlHarnessQueryOperation`; use the current type if it changes before implementation.

`SqlHarnessModule` handles `SqlHarnessMutationOperation` like this:

1. Resolve the target; classify with `SqlUsage.Mutation`.
2. If the decision is denied, return a safety rejection (exit 2).
3. If the tier is `ReadOnly` or `Session`, return `not_a_mutation`.
4. If `ExpectDatabase` is set and differs from the resolved database (ordinal), return `database_mismatch` (exit 4).
5. If `DryRun`, return `SqlHarnessDryRunReport` with `Command = "mutation"` and `Tier`.
6. If `Approval` is null, return a `MutationRequest` value for the caller to submit. The module never executes without an approval. Wire this as a `SqlHarnessMutationPrepared` result type that the CLI and MCP hand to `IMutationBrokerClient`.
7. If `Approval` is set:
   - recompute the digest;
   - on mismatch, throw `SqlHarnessSafetyException("Approval does not match the request.")`;
   - otherwise execute through the existing query path with `resolved with { Auth = approval.Writer }`, `AllowMutation = true`, `ConfirmDatabase = resolved.Database` (PostgreSQL then selects `ApprovedMutation` mode), and the classifier usage `Mutation`.

- [x] **Step 1: Write the failing tests**
  - Policy table: every mode × tier pair, `Dml`..`Opaque`.
  - Digest:
    - stable across dictionary insertion order;
    - changes when one SQL byte changes (trailing space included);
    - changes when one parameter value changes;
    - changes when the type of a `NULL` parameter changes.
  - Operation, against `FakeSqlExecution`/`FakeSession` used by `QueryTests`; find them with `grep -rn "class Fake" tests/SqlHarness.Tests`:
    - an unapproved operation never opens a session;
    - an approved one with a wrong digest throws;
    - `SELECT 1` gives `not_a_mutation`;
    - an `ExpectDatabase` mismatch gives exit 4;
    - a dry run gives tier `dml`.
- [x] **Step 2: Run them to verify they fail.**
- [x] **Step 3: Implement.** Keep the canonical JSON writer private to `MutationRequest`. Use `Utf8JsonWriter` with properties in fixed order and parameters encoded as `{name,type,size,precision,scale,value}`.
- [x] **Step 4: Run them to verify they pass**, plus `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Query|FullyQualifiedName~Postgres"`.
- [x] **Step 5: Commit**: `git commit -m "Add mutation policy, frozen request digest and approval-gated execution in Core"`.

### Task 3: Journal `mutation_requests`

**Files:**
- Modify: `src/SqlHarness.Core/Journal/JournalSchema.cs` (`Version13`, `CurrentVersion = 13`), `ActivityJournal.cs` (migration and writers), `IActivityJournal.cs`, `JournalModels.cs`, `src/SqlHarness.Dashboard/JournalReader.cs` and `DashboardModels.cs` (bounded history read seam)
- Test: `tests/SqlHarness.Tests/Journal/MutationJournalTests.cs`, the journal migration test that pins `CurrentVersion`

**Interfaces:**
- Produces: `void RecordMutation(MutationJournalRecord record)` and `void UpdateMutation(string requestId, string state, string? status, string? verification, DateTimeOffset at)`, where:

```csharp
public sealed record MutationJournalRecord(
    string RequestId, string SessionKey, string Profile, string Engine, string Server, string Database,
    string Tier, string Mode, string Digest, string? Sql, string? ParametersJson, DateTimeOffset CreatedAt);
```

`VersionN` DDL:

```sql
CREATE TABLE mutation_requests (
  id INTEGER PRIMARY KEY,
  request_id TEXT NOT NULL UNIQUE,
  session_key TEXT NOT NULL,
  host_pid INTEGER NOT NULL, host_started_at TEXT NULL,
  profile TEXT NOT NULL, engine TEXT NOT NULL, server TEXT NOT NULL, database_name TEXT NOT NULL,
  tier TEXT NOT NULL, mode TEXT NOT NULL, digest TEXT NOT NULL,
  sql_text TEXT NULL, parameters_json TEXT NULL,
  state TEXT NOT NULL, status TEXT NULL, verification TEXT NULL,
  created_at TEXT NOT NULL, decided_at TEXT NULL, finished_at TEXT NULL);
CREATE INDEX ix_mutation_requests_created ON mutation_requests (created_at);
```

`sql_text` and `parameters_json` are stored only when `journal.storeSensitive` is on; otherwise they are NULL. Retention applies the same age and size rules as operations, while preserving live `pending`, `approved`, and `executing` rows by their journal host PID and start time; abandoned in-flight and terminal rows remain eligible. Extend `JournalRetention` and its tests.

- [x] **Step 1: Write the failing tests**:
  - migration from N-1 to N;
  - insert then update;
  - SQL is NULL when `storeSensitive` is off;
  - a retention pass deletes old rows.
- [x] **Step 2: Run them to verify they fail.**
- [x] **Step 3: Implement.**
- [x] **Step 4: Run** `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~Journal`. Expected: PASS.
- [x] **Step 5: Commit**: `git commit -m "Journal mutation requests (schema v13)"`.

### Task 4: `MutationBroker` state machine

**Files:**
- Create: `src/SqlHarness.Dashboard/Mutations/MutationBroker.cs`
- Test: `tests/SqlHarness.Tests/Dashboard/MutationBrokerTests.cs`

**Interfaces:**
- Consumes: `MutationRequest`, `MutationPolicy`, `MutationStatus`, `TimeProvider` (use `Microsoft.Extensions.Time.Testing.FakeTimeProvider` if it is already referenced by the tests; otherwise a small fake in the test project).
- Produces:

```csharp
internal enum MutationState { Pending, Approved, Denied, Expired, Cancelled, Executing, Succeeded, Failed, OutcomeUnknown }

internal sealed record PendingMutationView(string RequestId, MutationRequest Request, string Environment, bool WriterSameAsReader,
    DateTimeOffset ExpiresAt);

internal sealed class MutationBroker(TimeProvider time, Func<MutationRequest, CancellationToken, Task<MutationBrokerResult>> execute)
{
    internal event Action<BrokerEvent>? Changed;   // feeds SSE: Added(view), Removed(requestId, state)
    internal IReadOnlyList<PendingMutationView> Pending { get; }
    /// <summary>Completes with the terminal result. Cancelling ct before execution starts cancels the request.</summary>
    internal Task<MutationBrokerResult> SubmitAsync(MutationRequest request, MutationDecisionKind kind,
        TimeSpan ttl, PendingMutationView view, CancellationToken ct);
    /// <summary>False when the request is no longer Pending (expired, cancelled, decided).</summary>
    internal bool TryApprove(string requestId, string verification);
    internal bool TryDeny(string requestId);
}
```

Rules:
- One lock guards all transitions.
- `Execute` kind → straight to `Executing`.
- `AskOperator` →
  1. if this session already has a pending request with a different digest → `busy`;
  2. if a pending request has the same digest → attach to its task (join);
  3. if a denial for this digest was recorded at most 60 s ago → `denied_by_operator`;
  4. otherwise create `Pending`, raise `Added`, start the TTL timer with `time.CreateTimer`.
- Expiry, cancellation, denial and approval each `TryTransition(Pending → X)` under the lock. Only the winner acts.
- Before `execute`, take the per-profile executing slot. If it is taken → `busy`.
- Cancellation:
  - before `Executing`, the caller's `ct` → `Cancelled`;
  - after `Executing` starts, `ct` is ignored and the result is still journaled (by the executor).

- [x] **Step 1: Write the failing tests** (with a fake time provider and an `execute` delegate that counts calls):
  1. Approve → executes once → `success`.
  2. Deny → `denied_by_operator`; the delegate is never called.
  3. Advance time past the TTL → `approval_timeout`; a later `TryApprove` returns false; delegate count is 0.
  4. **Race:** advance to exactly the TTL and call `TryApprove` on another thread at the same time, 200 iterations. The delegate count equals the number of `success` results, and every result is either `success` or `approval_timeout`.
  5. Cancel the caller's token while pending → `Cancelled`, with a `Removed` event; `TryApprove` returns false.
  6. Same digest submitted twice while pending → one `Added` event, and both tasks get the same result.
  7. Second different request in the same session → `busy`.
  8. Resubmission within 60 s after a deny → `denied_by_operator` with no event; at 61 s → a new `Pending`.
  9. `yolo`+`Dml` kind `Execute` → no `Added` event.
  10. Two profiles execute concurrently; the same profile → `busy`.
- [x] **Step 2: Run them to verify they fail.**
- [x] **Step 3: Implement.**
- [x] **Step 4: Run them to verify they pass** (run the race test 5 times: `--filter …MutationBrokerTests` in a loop).
- [x] **Step 5: Commit**: `git commit -m "Add the dashboard mutation broker state machine"`.

### Task 5: `MutationExecutor`

**Files:**
- Create: `src/SqlHarness.Dashboard/Mutations/MutationExecutor.cs`
- Modify: `src/SqlHarness.Dashboard/Mutations/MutationBroker.cs` (forward private request id and execution origin), `src/SqlHarness.Core/OperationFailureMapper.cs`, `src/SqlHarness.Core/Contracts.cs`, `src/SqlHarness.Core/SqlHarnessModule.cs` (mark sent-command transport failures)
- Test: `tests/SqlHarness.Tests/Dashboard/MutationExecutorTests.cs`, `tests/SqlHarness.Tests/Dashboard/MutationBrokerTests.cs`, `tests/SqlHarness.Tests/Postgres/PostgresReadOnlyRuntimeTests.cs`

**Interfaces:**
- Consumes: Task 2 `SqlHarnessMutationOperation` and `ApprovedMutation`; Task 3 journal; Task 1 profile; Core `SqlHarnessModule`.
- Produces: `internal sealed class MutationExecutor` with the two-argument compatibility method and a context overload. The broker carries an internal `MutationExecutionContext(RequestId, Origin)`; neither value is added to public `MutationRequest`.

Execution order:

1. Re-read the profile. If its mode is `never` now → `mutations_disabled`. If the mode is `yolo` and the tier is not `dml` while the request had been auto-approved → `mutations_disabled` (defensive).
2. If the tier is `privilege`, resolve the reader principal with a read-only query through `SqlHarnessModule` using the profile's own credential: `SELECT SUSER_SNAME() AS l, USER_NAME() AS u` on SQL Server, `SELECT current_user AS l, session_user AS u` on PostgreSQL. If any grantee equals one of those values (OrdinalIgnoreCase) → `principal_protected`. If the query fails → `approval_unavailable`, with the message "Could not resolve the reader principal", and execution stops.
3. Build the writer `AuthSpec`:
   - `SameAsReader` → the resolved reader `AuthSpec`;
   - otherwise the reader's strategy with `SqlUser` and `PasswordEnvVar` replaced.
   
   A missing env var → `failed`, with the message naming the variable (not the value).
4. Journal state `Executing`, then call `SqlHarnessModule.ExecuteAsync(new SqlHarnessMutationOperation(...) { Approval = new ApprovedMutation(request.Digest, writer) })`.
5. Map the outcome:
   - success → `success` with the query result;
   - a SQL error → `failed`;
   - connection loss or timeout after the command was sent → `outcome_unknown`. Use the existing `OperationFailureMapper` categories, and add one if it does not distinguish "sent" from "not sent". A connect failure before sending is `failed`.
6. Journal the terminal state. Never retry.

- [x] **Step 1: Write the failing tests** with a fake module/execution:
  - the mode changed to `never` → `mutations_disabled` with no execution;
  - a grantee equals `USER_NAME()` → `principal_protected`;
  - a writer env var is missing → `failed` and the message contains the variable name;
  - the fake throws a connection-lost error after send → `outcome_unknown`, executed once;
  - success → journal `Succeeded`.
- [x] **Step 2: Run them to verify they fail.** The initial focused run failed to compile because the executor and execution context were not present.
- [x] **Step 3: Implement.**
- [x] **Step 4: Run them to verify they pass.** Focused executor, broker-context, and failure-mapper tests passed; full plan gates remain Task 13.
- [x] **Step 5: Commit**: `git commit -m "Execute approved mutations with the writer credential and protect the reader principal"`.

### Task 6: Dashboard endpoints and client

**Files:**
- Create: `src/SqlHarness.Dashboard/Mutations/MutationEndpoints.cs`, `src/SqlHarness.Dashboard/Mutations/HttpMutationBrokerClient.cs`
- Modify: `src/SqlHarness.Dashboard/DashboardServer.cs:170-226` (routes), `src/SqlHarness.Dashboard/DashboardSecurity.cs:56-151` (new guarded paths and bearer submit)
- Test: `tests/SqlHarness.Tests/Dashboard/MutationEndpointsTests.cs` (follow the existing dashboard `WebApplicationFactory`/test-host pattern in `tests/SqlHarness.Tests/Dashboard`)

**Interfaces:**
- Produces: these routes.

| Route | Auth | Behaviour |
|---|---|---|
| `POST /api/mutations` | `Authorization: Bearer <dashboard token>` + JSON; no cookie needed | body = `MutationRequest` fields. The server re-resolves and re-classifies through `SqlHarnessModule` (a dry-run of a `SqlHarnessMutationOperation`) and rejects a body whose tier, server or database differs. Applies the policy, then long-polls `MutationBroker.SubmitAsync` with `RequestAborted` as `ct`. Returns `MutationBrokerResult` JSON. HTTP 200 for every terminal status. |
| `GET /api/mutations/events` | cookie | SSE: `added` (a `PendingMutationView` with SQL and parameter values) and `removed`. The first event is a snapshot of the current pending list. |
| `POST /api/mutations/{id}/approve` | guarded write (cookie + `X-SqlHarness-Dashboard` + JSON + origin) | body `{}` only. 409 if the request is no longer pending. |
| `POST /api/mutations/{id}/deny` | guarded write | 409 if it is no longer pending. |
| `GET /api/mutations` | cookie | journal history page (cursor and limit like `/operations`). |

`HttpMutationBrokerClient : IMutationBrokerClient`:
1. Read the dashboard endpoint and token through the existing `DashboardLock`/`DashboardToken` readers.
2. If no dashboard is running and `dashboard.autoStart` is on, launch it with `IDashboardLauncher` and poll the lock for up to 10 s. Otherwise return `approval_unavailable`.
3. POST with `HttpClient.Timeout = TTL + timeout + 30 s`, reporting progress every 5 s.
4. Map an HTTP failure before a response to `approval_unavailable`. A dropped connection after the request was sent maps to `outcome_unknown`, because the broker may have executed.

- [x] **Step 1: Write the failing tests:**
  - submit without bearer → 401;
  - submit with a body whose database differs from the resolution → 400 `database_mismatch`;
  - approve without the write header → 403;
  - approve with a cookie → 200, and the long-poll returns `success`;
  - a second approve → 409;
  - SSE receives `added` and then `removed`;
  - the client, with no dashboard and autostart off → `approval_unavailable` without waiting;
  - the submitting client aborts → the broker shows `Cancelled`.
- [x] **Step 2: Run them to verify they fail.**
- [x] **Step 3: Implement.** In `DashboardSecurity`, list the new POST paths explicitly. Keep the 405 for every other non-read path.
- [x] **Step 4: Run** `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~Dashboard`.
- [x] **Step 5: Commit**: `git commit -m "Serve mutation submit, events and decisions from the dashboard"`.

### Task 7: CLI `mutation`; remove `--allow-mutation`

**Files:**
- Create: `src/SqlHarness.Cli/Commands/MutationCommand.cs`
- Modify: `src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs:225-247` (drop `--allow-mutation`/`--confirm-database`; pass `AllowMutation: false, ConfirmDatabase: null`), `src/SqlHarness.Cli/SqlHarnessCli.cs:64-77` (register `mutation`), `src/SqlHarness.Cli/Commands/Renderer.cs` (render mutation statuses), `src/SqlHarness.Cli/Commands/CapabilitiesCommand.cs`
- Test: `tests/SqlHarness.Tests/Cli/MutationCommandTests.cs`; update tests that use `--allow-mutation` (`grep -rn "allow-mutation" tests`)

**Interfaces:**
- Consumes: Task 2 and Task 6 `HttpMutationBrokerClient`.
- Produces: `sqlharness mutation` with the target options of `query`, `--file`/stdin, `--param`, `--timeout`, `--max-rows`, `--expect-database <name>`, `--dry-run` and the output options of `query`.

Flow:
1. Module prepare (Task 2).
2. Then `IMutationBrokerClient.SubmitAsync`, with progress printed to stderr: one line "Waiting for operator approval in the dashboard (up to N s)…", repeated every 30 s at most.
3. Render the envelope:
   - `status` = the mutation status;
   - `result` = the query result on success;
   - `error.message` = the broker message.
4. Exit codes per Global Constraints.

`query` keeps the static dry-run behaviour. A persistent DML batch in `query` now fails with a hint: "Persistent changes use `sqlharness mutation`."

- [x] **Step 1: Write the failing tests** with a fake `IMutationBrokerClient` injected through the command's DI:
  - success → exit 0 and the result is printed;
  - `denied_by_operator` → exit 2;
  - `database_mismatch` → exit 4;
  - `outcome_unknown` → exit 5;
  - `--dry-run` → tier and no submit;
  - `query --allow-mutation` → a parse error (unknown option);
  - `query` with `DELETE` → exit 2 with the hint.
- [x] **Step 2: Run them to verify they fail.**
- [x] **Step 3: Implement.**
- [x] **Step 4: Run** `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~Cli`.
- [x] **Step 5: Commit**: `git commit -m "Add the mutation command and remove query --allow-mutation"`.

### Task 8: MCP `sqlharness_mutation`

**Files:**
- Modify: `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` (new tool next to `QueryAsync` at 60-95; register in the name list at 479-486), `src/SqlHarness.Mcp/McpOperationMapper.cs` (a `MapMutationAsync` mirroring `MapQueryAsync` at ~256), `src/SqlHarness.Mcp/McpLimits.cs:27` (`MaxTools = 9`), `src/SqlHarness.Cli/Commands/McpServeCommand.cs` (inject `HttpMutationBrokerClient`)
- Test: `tests/SqlHarness.Mcp.Tests/McpMutationToolTests.cs`, catalog budget and tool-count tests

**Interfaces:**
- Produces: tool `sqlharness_mutation` with arguments `sql`/`file`, `parameters`, `timeout`, `maxRows`, `expectDatabase`, `maxResultBytes`, `maxOperationSeconds` and `dryRun`, using the same descriptions as `sqlharness_query` where they mean the same thing.

Tool description (keep it short, because the catalog budget counts it):

> "Persistent change (DML/DDL/GRANT/EXEC) on the scope's profile. The operator's dashboard policy decides; the call waits up to the approval timeout. Any status other than success: stop and report; do not retry."

While waiting, send progress through the existing `McpProgressReporter`, stage `awaiting_approval`, every 5 s. The result uses the same agent envelope as `sqlharness_query`. `status` carries the mutation status, and `isError` is true for everything except `success`.

- [x] **Step 1: Write the failing tests:**
  - `tools/list` contains 9 tools within budget;
  - the call with a fake broker returns `success` with result sets;
  - `approval_timeout` → `isError=true`, `status:"approval_timeout"`;
  - unknown arguments such as `approved` or `confirmDatabase` are rejected by `ThrowIfUnknown`;
  - progress notifications are sent while waiting.
- [x] **Step 2: Run them to verify they fail.** Catalog RED: expected 9 tools, received 8.
- [x] **Step 3: Implement.**
- [x] **Step 4: Run** `dotnet test tests/SqlHarness.Mcp.Tests` (400 passed, 4 skipped).
- [x] **Step 5: Commit**: `git commit -m "Add the sqlharness_mutation MCP tool"`.

### Task 9: Approval UI: toast, notification, dialog

**Files:**
- Create: `src/SqlHarness.Dashboard/ui/src/mutations/useMutationEvents.ts`, `mutationStore.ts`, `ApprovalToast.tsx`, `ApprovalDialog.tsx`, tests next to each
- Modify: `src/SqlHarness.Dashboard/ui/src/components/AppLayout.tsx` (mount the toast host and dialog), `ui/src/api/{types,client}.ts`

**Interfaces:**
- Consumes: Task 6 `GET /api/mutations/events`, `POST /api/mutations/{id}/approve|deny`.
- Produces: `usePendingMutations(): PendingMutation[]` and `<ApprovalDialog requestId />`.

Behaviour:
- **Event stream:** `EventSource('/api/mutations/events')` with reconnect handled like `live/useLiveFeed.ts`. Snapshot events and reconnect replay synchronize visible requests but never trigger sounds or new notifications. Track request IDs already observed by the live stream; a digest join emits no second `added` event and causes no second ping.
- **Toast and ping:** each newly observed live `added` event shows a toast titled "A change needs your decision" with a "Review" button and no SQL. Play the selected sound once for that event. When the tab is hidden and `Notification.permission === "granted"`, also `new Notification("SQLHarness: a change needs your decision")`; its click focuses the window and opens the dialog. Browser autoplay rejection is caught and ignored; it must not block the toast, notification, approval controls, or request handling. The next user-gesture preview in Settings remains available.
- **Dialog content:**
  - environment badge in large type (red for prod/staging);
  - a persistent `AppLayout` banner, "YOLO is on for production profile X", whenever `GET /api/profiles` reports an acknowledged production yolo profile;
  - profile, server and database;
  - tier badge and a "same account as reader" warning when it applies;
  - full SQL through the existing `SqlBlock` (which renders text, not HTML);
  - a parameters table (name, type, value as text);
  - digest prefix (12 chars) and a countdown;
  - a multi-statement warning.
- **Dialog controls:**
  - Approve and Deny buttons;
  - Approve is disabled for 1 s after open and after expiry;
  - Enter does nothing;
  - Escape closes without deciding.
- `removed` closes the toast and the dialog with the terminal state shown briefly.

- [x] **Step 1: Write the failing tests:**
  - an `added` event shows a toast without SQL text;
  - the initial SSE snapshot and reconnect snapshot synchronize the request list without playing sound or issuing a new notification;
  - a newly observed live `added` event plays once, and a duplicate/digest join does not replay it;
  - rejected browser autoplay leaves the toast and approval controls usable;
  - the dialog renders `<script>alert(1)</script>` from SQL and a parameter value as literal text, and no `script` element exists in the DOM;
  - Approve is disabled for 1 s (fake timers);
  - Enter does not call approve;
  - a 409 on approve shows "This request is no longer pending";
  - `removed` closes the dialog.
- [x] **Step 2: Run them to verify they fail**: `npm test -- mutations/Approval.test.tsx` failed at the not-yet-created `./sound` module.
- [x] **Step 3: Implement.**
- [x] **Step 4: Run them to verify they pass**, then build the UI as described in README › Building the dashboard UI. Verify the default Warm chime plays once for a new live event, not for initial SSE snapshots, reconnects, or digest joins; an autoplay restriction must not disrupt approval.
- [x] **Step 5: Commit**: `git commit -m "Show mutation approvals as toasts with a review dialog"`.

### Task 10: Mutations history and settings UI

**Files:**
- Create: `src/SqlHarness.Dashboard/ui/src/pages/MutationsPage.tsx` + test
- Modify: `ui/src/router.tsx`, `ui/src/components/AppLayout.tsx` (nav), `ui/src/pages/SettingsPage.tsx` (section "Mutations": default mode, click-only verification display, approval timeout, sound selection and custom sound, "Enable browser notifications" button calling `Notification.requestPermission()`), `ui/src/pages/ProfilesPage.tsx` (mode, environment, writer kind)

**Interfaces:**
- Consumes: `GET /api/mutations`, `GET/PUT /api/settings`, `GET /api/profiles`.

Sound settings are browser-local and do not add server-side file paths or upload routes. Offer three pleasant built-in sounds (default: "Warm chime"), each with a user-gesture preview, plus mute and a custom local audio file. Accept MP3, WAV, or OGG files up to 2 MiB; validate both file type/extension and size, and persist the chosen option and custom bytes in IndexedDB. Reject invalid files with an inline explanation. Preview is user-initiated so browser autoplay restrictions do not affect approval. Verification displays only `click`; unsupported values are rejected, never silently downgraded.

- [x] **Step 1: Write the failing tests:**
  - the history table lists time, profile, database, tier, mode, status and verification;
  - a row that ran without asking (`verification` null, mode `yolo`) on a prod/staging-labelled profile carries a warning badge "ran without approval on production";
  - SQL appears only when it was stored, as text;
  - a `<script>` literal stays text;
  - the settings section saves `approvalTimeoutSeconds` on blur and rejects 10 with the server's message;
  - the Settings page offers three built-in sounds with previews, mute, and custom local MP3/WAV/OGG up to 2 MiB; invalid types/sizes are rejected; preferences and custom audio persist in IndexedDB; no server path or upload is involved;
  - unsupported verification values are rejected with an explanation and do not silently change the current setting.
- [x] **Step 2: Run them to verify they fail.**
- [x] **Step 3: Implement.**
- [x] **Step 4: Run them to verify they pass.**
- [x] **Step 5: Commit**: `git commit -m "Add mutation history and mutation settings to the dashboard"`.

### Task 11: Deferred to plan 078 — WebAuthn verification

This task is intentionally out of scope for 073. Windows Hello/WebAuthn, package selection, enrollment and assertion routes, digest-bound approval, credential storage and lifecycle, Settings transitions, and recovery are assigned to [plan 078](078-mutation-approval-webauthn.md), which depends on the click-only broker in 073. No WebAuthn package or endpoint is introduced here. Tasks 12 and 13 retain their existing numbers.

### Task 12: Agent-side hygiene hook

**Files:**
- Modify: `scripts/hooks/protect_sqlharness_config.py`, `scripts/hooks/test_protect_sqlharness_config.py`

Extend the blocklist (the hook stays fail-open on bad JSON, as documented):
- reads of `dashboard-token` and `dashboard.json` by any tool. Handle `Read`/`read_file` tool names with a `file_path`/`path` argument, besides Bash.
- writes to `config.json` and `targets.json` under `.sqlharness` by `Write`/`Edit` tools or shell redirection (`>`, `Set-Content`, `Out-File`).
- shell commands calling `/api/mutations/` on `127.0.0.1` or `localhost`.

- [x] **Step 1: Write the failing tests** (unittest, one case per new rule, plus negative cases: reading `targets.json` is allowed, and so is a curl to another port path).
- [x] **Step 2: Run them to verify they fail**: `python -m unittest discover -s scripts/hooks -p 'test_*.py' -v`. The requested pytest runner was unavailable; the standard-library runner observed 3 failures and 10 errors before implementation, then all 17 tests passed.
- [x] **Step 3: Implement.**
- [x] **Step 4: Run them to verify they pass.**
- [x] **Step 5: Commit**: `git commit -m "Extend the SQLHarness agent hook to mutation and settings files"`.

### Task 13: Docs, capabilities, skill, bookkeeping

**Files:**
- Modify:
  - `README.md`: Safety contract › Mutations rewritten; the threat model in the spec §1 wording; `acknowledgeProductionYolo`, and the risk that a profile whose variables switch between dev and production keeps one `environment` label; the parity rule extended to additions at the `README.md:178` paragraph; Dashboard section for approvals, notifications and sound settings; Settings fields; exit codes; dry-run `mutation`. State that verification is click-only in 073 and point to plan 078 for deferred WebAuthn.
  - `docs/mcp.md`: replace "No persistent mutations" with the mutation tool contract; tool count 9; catalog budget.
  - `src/SqlHarness.Core/Capabilities.cs` and the MCP capabilities: active profile mode, `writerConfigured` (bool), verification, TTL, `dashboardReachable`.
  - `skills/sqlharness/SKILL.md`: use `query` to read and `mutation` for persistent changes; on any non-success status stop and report; never retry; never reach for another transport.
  - `AGENTS.md` › Database work › Writes: "Persistent changes go through `mutation`; the operator's dashboard policy approves them. Never supply approval yourself."
  - `docs/superpowers/specs/2026-09-26-windows-approval-tray-design.md` and `docs/superpowers/plans/2026-09-26-audit-08-windows-approval-tray.md`: status line "Superseded by plan 073 (dashboard approval); tray deferred."
  - `docs/superpowers/plans/2026-09-26-audit-roadmap.md`: row 08 status.
  - `plans/README.md` and `plans/history.md`; this plan's Status.
- Test: `DocsSyncTests`, `ContractsTests`, MCP catalog tests, skill tests if any (`grep -rln "SKILL.md" tests`)

- [x] **Step 1: Update docs and capabilities.** Docs/Contracts/Capabilities filter passed 131/131; MCP suite passed 401, skipped 4, failed 0.
- [x] **Step 2: Windows gate.** Final product revision `2085f0ff` passed `just verify -LogDirectory D:/temp/sqlharness-plan073-finalfix-20261016-1450`: exit 0, six stages, 8m19s; UI 260 passed, Core/CLI 3960 passed/11 skipped, MCP 401 passed/4 skipped. Build and formatting passed. Sol low whole-branch review and scoped re-review approved. Linux was not run at the operator's explicit direction.
- [x] **Step 3: Commit**: commit only the explicit product, test, and documentation paths; do not stage the Task 13 scratch report.

## Completion evidence

Implemented range: `b3cdf315..2085f0ff`. The final combined fix addresses audio-constructor feedback, busy approval feedback, synchronized race starts, a real-Core matching-digest writer/session test, and README summaries. Warm chime is the operator-selected default; all three bundled options, previews, mute and browser-local custom audio are available. Task 11 is deferred to [078](078-mutation-approval-webauthn.md).

Verification is feature-branch evidence; no local merge, push, publish or live database run occurred. Final UI logs retain non-failing localhost:3000/Happy DOM diagnostics and Node DEP0190; prior Plan 071 completion evidence already records localhost EventSource and DEP0190 output. Happy DOM teardown provenance remains unverified. The Windows gate is passing, not a claim of pristine console output. The runtime-capability implementation preceded its new tests; the actual sequence is recorded without claiming test-first RED.


## Local integration — 2026-10-16

Merged locally into `main` at `bb2fa5d` (parents `82b4a3e` and `8ebee2d`). The index/history conflicts preserve completed plans 063 and 076 and existing plan 077 for agent optimization evidence. Deferred WebAuthn is renumbered to [078](078-mutation-approval-webauthn.md), with references updated; no WebAuthn implementation was added. All 170 local Markdown links in the affected documentation were checked.

Post-merge Windows `just verify -LogDirectory D:/temp/sqlharness-plan073-main-verify` exited 0 on `bb2fa5d`: six stages in 8m10s; UI 260 passed, Core/CLI 4006 passed/11 skipped, MCP 402 passed/4 skipped; build succeeded with zero warnings/errors. UI install was skipped (unchanged lockfile), formatting skipped (clean tree, zero changed C# files). Linux was not run. No push, installation, publication or live database execution. The feature worktree/branch are retained because their ignored scratch reports had deletion blocked by automatic approval review.
