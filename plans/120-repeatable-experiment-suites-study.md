# Plan 120: Evaluate explicit manifests for repeatable comparison suites

> **For agentic workers:** Use `superpowers:executing-plans`, or delegated execution when selected. Execute this as a design/dispatch-simulation study. Do not build a production suite runner, invoke SQLHarness against a target, or turn journal history into executable work.
>
> **Drift check:** `git diff --stat 5f7342a7..HEAD -- src/SqlHarness.Core/Contracts.cs src/SqlHarness.Core/ParameterSetResolver.cs src/SqlHarness.Core/ParameterSetFileReader.cs src/SqlHarness.Cli/SqlHarnessCli.cs src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs docs/mcp skills/sqlharness/SKILL.md`
> Reconcile compatible drift. Stop for a changed target/parameter contract, ambiguity that changes execution semantics, or broader authorization requirements.

## Status

- **Status:** RESEARCH COMPLETE — choose C (no new supported feature now); defer B pending the explicit demand and design gates in [decision](120-evidence/decision.md). Current evaluator/verifier evidence: 29 synthetic cases and 17 mock dispatches, including strict integer-version validation; fixed typed scalar and MCP named-set-file shapes only, with no full reader grammar parity or executed symlink proof. The initial whole-branch review is approved after its single Minor correction; final scoped rereview is pending. Local `plan-120` only; not integrated or pushed.
- **Priority:** P3; selected direction proposal 5; recurring multi-subject demand remains unconfirmed.
- **Effort / risk / confidence:** M study, L potential implementation / MED-HIGH / MED grounding.
- **Depends on:** none of 116-119. Existing compare/parameter-set contracts are implemented; Plan 036 is a different testing-infrastructure feature.
- **Planned at:** `5f7342a7f5d48e03268491059949d1edfc24c65c`, 2026-10-25.
- **Goal:** Decide whether an explicit external manifest improves repeatability enough to justify a supported suite surface over existing comparisons.
- **Architecture:** An operator-authored manifest references exact input bytes and existing operations. Simulate sequential dispatch with injected outcomes; preserve each operation identity and partial failure. Do not add a universal suite success or equivalence verdict.
- **Tech stack:** Existing .NET 10 operation/parameter models; PowerShell pure input validation and mock dispatch; JSON synthetic fixtures.
- **Spec:** Study candidate and fixed scenario requirements below; deliver `120-evidence/design.md` for accepted scope.

## Execution setup

Use an isolated `plan-120` checkout after inspecting `git status --short` and `git worktree list --porcelain`. Run `$studyStart = (git rev-parse HEAD).Trim()` before changes and retain the revision/initial status. Preserve unrelated work; no automatic commit/merge/push. Apply `docs/agents/verification.md` scoped study checks. Evaluator/verifier scripts must return nonzero for missing evidence or failed assertions.

## Why this matters

The README asks users to inventory representative parameter populations and perform repeated comparisons, but commands describe individual SQL pairs. A recurring multi-subject task may require rebuilding invocations and remembering which file versions were measured. A manifest could help, but it also risks becoming an unnecessary orchestration/deployment framework.

## Current state and conventions

- `README.md:282` asks for common/rare/boundary/empty cases outside the application repository. `README.md:298-302` describes sequential per-set execution, first-failure stop and shared order-dependent cache state.
- `src/SqlHarness.Cli/SqlHarnessCli.cs:71-80` registers individual query, mutation, measure, compare, plan, artifact, watch, snapshot, capabilities and doctor commands; no suite command exists.
- `src/SqlHarness.Core/Contracts.cs:100` defines one baseline/candidate pair:

```csharp
public sealed record SqlHarnessCompareOperation(
    SqlTargetRequest Target,
    string? SetupSql,
    string BaselineSql,
    string CandidateSql,
    IReadOnlyList<string> Parameters,
    int TimeoutSeconds,
    int Repeat,
    ResultComparisonMode CompareResults = ResultComparisonMode.Ordered,
    bool CompareNullability = false) : SqlHarnessOperation
```

- The matrix operation at line 117 also supports typed matrix/named parameter sets. Reuse their resolver semantics rather than inventing a new parameter type grammar.
- `src/SqlHarness.Core/ParameterSetFileReader.cs:20` uses bounded readers and existing BOM/comment/trailing-comma rules. Fixed parameter JSON and set files share the existing ingress budget.
- `skills/sqlharness/SKILL.md` requires matching application shapes, sequential dependent work, separate execution/equivalence/performance statuses, explicit gaps and no automatic retries after ambiguous failures. Keep those meanings authoritative.
- `plans/109-evidence/task-3-decision.md:24` prohibits cross-artifact reuse. Each suite entry is a fresh, explicit requested operation with its own artifact; a matching SQL hash is not permission to reuse another execution's evidence.

## Global constraints

- Only synthetic ticket workspaces generated under this plan's evidence folder; no real tickets, profiles, secrets, journals or databases.
- No mutation entry, automatic index action, deployment, cache clear, concurrency, retry, target substitution, SQL rewriting or inference of cases from production data.
- Preserve existing operation-level parameter bounds, safety classification, target identity, timeouts, input roots and artifact ownership. Preflight is not execution approval or proof of live permissions.
- Keep technical SQL/parameters/results readable under existing bounds. Record no credential values in manifests or logs.
- Do not change CLI/MCP parity or add a new tool as a shortcut. Any supported future surface must have an explicit design decision.

## Review focus

1. Paths, symlinks, changed input files and file hashes must not evade current input-root or size rules.
2. One failed, cancelled or timed-out entry stops dispatch; subsequent entries are not executed, not equivalent or failed measurements.
3. A completed negative comparison is valid evidence; its regression/equivalence result must survive aggregation.
4. Named sets/matrix/TVP/list inputs must retain existing case/session semantics and limits rather than being expanded into a different experiment silently.
5. Resumed runs, stale files and partial previous artifacts never cause an implicit retry or evidence reuse.

## Scope and output files

**Only writable:** this plan/index section and `plans/120-evidence/` with `inventory.md`, `design.md`, `decision.md`, `cases.json`, `evaluate.ps1`, `verify.ps1`, `results.json`, `fixtures/` containing two synthetic ticket directories and mock outcome files, and logs. No product, existing test, README, skill, package or CI changes.

## Candidate manifest and simulator contract

Evaluate an external `experiment-suite.json` version 1 with an ordered `comparisons` array (candidate cap 16 entries, 1 MiB manifest bytes). Each entry has a unique `id`, explicit profile/vars target request, subject/scope, baseline/candidate and optional setup file references `{path, sha256}`, explicit repeat/timeout/comparison/nullability policies, and one existing fixed/case input form. The candidate cap is a study choice, not a product limit change.

Resolve paths relative to the manifest directory, verify exact raw-byte SHA-256 before dispatch, and preserve current rooted-input restrictions. Study fail-closed behavior for symlink/reparse escapes; if canonical-root reuse cannot be specified, reject that candidate. A hash pins file bytes only, not a database snapshot or evidence validity. Referenced parameter files must retain existing parse/aggregate budgets (16 MiB/file, 32 MiB raw per operation, separate materialization bounds).

Make the alternative-to-feature comparison explicit: A checked-in external command list/helper, B supported CLI manifest surface over existing Core operations, C no new feature. Distinguish CLI compare matrix support from MCP named-set input support; do not generate unsupported CLI flags or convert sets to separate operations while claiming identical semantics. A surface may be deferred for a particular input shape. Pin exact accepted grammar and integration seam only after the source map proves parity.

The simulator is a pure function of synthetic manifest, fixture files and injected mock outcomes. It must not launch SQLHarness, a shell command from the manifest, an MCP client or a database. Output an ordered invocation inventory and per-entry execution/equivalence/performance/coverage identities; a later real runner would obtain operation/artifact IDs from actual results. Never synthesize a whole-suite equivalence or winner.

## Tasks

### Task 1: Establish demand assumptions and compare existing alternatives

- [x] Write `inventory.md` mapping every proposed input to the current CLI/Core/MCP contract and authority for bounds. Flag unsupported combinations explicitly.
- [x] Generate two portable synthetic ticket workspaces: A has two SQL subjects with small/large/empty scalar cases; B has three revision pairs with setup, boundary date, duplicate-sensitive results and a deliberately changed file. Include exact generated hashes; no placeholder digests in executable fixtures.
- [x] Count command/input reconstruction steps for a plain external command list versus a manifest. Label them synthetic ergonomics estimates, not observed user time or model tokens. Lack of real demand is a recorded limitation, not invented interview evidence.
- [x] Verify: `Select-String -Path plans/120-evidence/inventory.md -Pattern 'CLI|MCP|Core|named|matrix|bounds'` -> every topic covered; reopen cited contracts and option definitions.

### Task 2: Specify and exercise the safe dispatch model

- [x] Write `design.md`: exact candidate schema, input-root/hash semantics, mutually exclusive input rules, operation identity, deterministic dispatch order, partial outcomes and A/B/C decision matrix. State whether a target change is permitted and require it to be explicit per entry within existing operator scope.
- [x] Build `evaluate.ps1` with an injected in-memory dispatcher returning mock outcomes and recording calls. Never use `Invoke-Expression`, arbitrary commands, real target profiles or executable names from input. Output only synthetic invocation records and outcome metadata to `results.json`.
- [x] `cases.json` covers success and negative outcomes, stop/tail behavior, changed and missing inputs, invalid manifests, path escapes, unsupported inputs, reuse/resume and mutation rejection.
- [x] Simulate first-error/cancellation stop and preserve completed negatives. Preflight and immediate per-entry hash checks are explicit; prior completed entries survive an after-preflight change without claiming atomicity.
- [x] Implement independent `verify.ps1` assertions for every case: exact dispatch count/order, unchanged parameter/case semantics, preserved outcome IDs, no invocation for unexecuted tails, no retries/resume/reuse, and no real connection capability.
- [x] Verify evaluator and verifier exit 0; every case accounted for, with no external process or DB call. The OS denied test-symlink creation; results label the simulated rejection and do not claim executed filesystem proof. The final scoped version-gate correction adds three independently asserted malformed-version cases; current count is 29 cases and 17 mock dispatches.

### Task 3: Decide whether a supported feature is justified

- [x] Write `120-evidence/decision.md`: choose C for now and defer B; explain the hypothesized need, evidence limits, unsupported cases, coarse costs, and demand/design gates.
- [x] No feature is accepted; therefore no future product file map or test plan is presented as approved. A new scoped plan is required if later demand gates justify B.
- [x] Record future implementation verification requirements: focused CLI/compare/parameter resolver tests with zero DB connections, then Windows `just verify` and explicit branch formatting. Real target runs need separate authorization; Plan 036 is not a hard predecessor or substitute for user-workload acceptance.
- [x] Verify study scripts, relative links, whitespace and unchanged product paths; mark RESEARCH COMPLETE with selected alternative and limits. Whole-branch review remains pending.

## Done criteria

- [x] Two generated workspaces reproduce from repository-local synthetic inputs, with real computed input hashes and an explicit supported-input matrix.
- [x] Study verifier independently verifies all named scenarios, including string, boolean, and floating-point version rejection, stop/retention behavior and preflight policy.
- [x] A/B/C alternatives, coarse cost, demand assumptions and a go/no-go decision are documented; no unsupported CLI flag or hidden semantic conversion is claimed.
- [x] Product paths are unchanged; changed/untracked paths are scoped; links and whitespace pass.
- [x] No real suite, real target, mutation, runtime install, paid agent, CI dispatch or token-savings claim occurred.

## STOP conditions and maintenance

Stop if the desired workflow needs automatic retries, journal-based replay, inferred targets, persistent writes, a root-safety exception or parameter semantics that existing operations cannot preserve. A source gap supports deferral, not a new execution engine inside the spike. Future parameter/target contracts must update manifest validation from the same authoritative home. Use isolated `plan-120`; no merge/push/publication.
