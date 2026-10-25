# Plan 118 evidence inventory: retained conditions and comparison identity

Read-only product study at the current Plan 118 worktree tree. Paths and line references below were reopened against the current source after drafting. The inventory describes existing behavior only; it proposes no capture, retention, fingerprint, authorization, or comparison-policy change.

## Current payload builder excerpts

`ConditionsModelBuilder.Build` records operation, fixed policies, warmup, repeat, timeout, comparison mode, and nullability in the conditions payload:

```csharp
var execution = new ConditionsExecution(kind, "per-case", "per-case", 1, repeat, timeout, comparisonMode, compareNullability);
```

The regular operation fingerprint builder includes session policy, setup binding, and comparison mode:

```csharp
execution = new
{
    model.Execution.SessionPolicy,
    model.Execution.SetupBinding,
    model.Execution.ComparisonMode,
},
```

The measurement operation fingerprint builder omits comparison mode:

```csharp
execution = new
{
    model.Execution.SessionPolicy,
    model.Execution.SetupBinding,
},
```

The per-case regular and measurement builders use the same respective execution fields while fingerprinting that case's varying parameters. Across both payload families, engine, server, database, setup hash, fixed parameters, and varying parameters participate; `OperationFingerprintPayload` uses all cases' varying parameters, while `CaseFingerprintPayload` uses one case. Neither family includes `Operation`, `Warmup`, `Repeat`, `TimeoutSeconds`, or `CompareNullability`. See [ConditionsModelBuilder.cs](../../src/SqlHarness.Core/Journal/ConditionsModelBuilder.cs#L124), [line 144](../../src/SqlHarness.Core/Journal/ConditionsModelBuilder.cs#L144), [line 160](../../src/SqlHarness.Core/Journal/ConditionsModelBuilder.cs#L160), and [line 176](../../src/SqlHarness.Core/Journal/ConditionsModelBuilder.cs#L176).

The model explicitly retains settings outside the fingerprints:

```csharp
int Warmup,
int Repeat,
int TimeoutSeconds,
string? ComparisonMode,
bool? CompareNullability);
```

See [ConditionsModel.cs](../../src/SqlHarness.Core/Journal/ConditionsModel.cs#L24).

## Field inventory

| Field or identity | Retained / projected today | Fingerprint participation | Sensitivity and omission | Comparison use / explanation boundary |
|---|---|---|---|---|
| Engine, resolved server, database | Conditions payload and dashboard evidence | Included in both regular and measurement fingerprints, operation and case | Server/database remain readable under repository contract. They are target context, not credentials. | Dashboard group identity also separately groups by subject/database; compare identity resolution uses subject and database (see below). |
| Setup SQL | Setup hash retained; setup text retained only when `storeSensitive` is enabled | Content is HMACed into the setup hash, then hash participates in both fingerprint families | `storeSensitive=false` drops text while preserving hash. Oversize conditions may drop text and append `setup.text` to `omitted`. | A retained hash supports matching but cannot explain what setup changed. Do not infer its content. |
| Fixed and varying scalar parameters | Name, effective type, and value in conditions model; labels can summarize values when sensitive storage is enabled | Name/type/value included in fingerprint payload; fixed plus operation-wide varying values for operation fingerprints; fixed plus one case's varying values for case fingerprints | The builder creates fingerprint inputs with values regardless of `storeSensitive`; the display/persisted model uses that flag. Journal sealing HMACs the payload. With sensitive storage disabled, persisted scalar `value` is null; size bounding can omit it and records a field path. Do not infer missing values from labels or keyed fingerprints. | Exact value explanation is available only when retained. An `omitted` path identifies removed evidence. The UI text `(value not stored)` is ambiguous: it is emitted for both JSON null and a missing/undefined value, so it cannot establish that the original measurement value was absent. |
| Materialized list and TVP parameters | Name, kind, count, columns/column types, and, when sensitive storage is enabled, list values or TVP rows and source text; the value/source payload is also retained in an internal fingerprint-content field before sealing | Kind/rows/values/source text are serialized into `ContentHash` using the journal salt; that HMAC plus parameter metadata is carried in the regular and measurement fingerprint payload as a parameter object | `storeSensitive=false` omits rows, values, and source text from persisted conditions while keeping metadata and the keyed content hash. Oversize bounding can independently remove rows, values, or source text and append a field path. Never infer the materialized contents from labels or keyed hashes. | Matching can account for materialized content without exposing it. Explanations can name retained metadata; content-level differences are explainable only when both sides retain the corresponding source material. |
| `MeasurementFingerprint` | Persisted at operation and case level; reader uses the case measurement fingerprint for metric pairing, falling back to legacy fingerprint for old journals | Dedicated fingerprint variant omits comparison mode; otherwise uses context, setup hash, fixed/current-case parameter payload, session policy, setup binding | HMAC over journal-local salt and canonical payload. It is an opaque matching identity, not a reversible digest or explanation source. Legacy models fall back to `Fingerprint`. | `OptimizationComparer.CaseKey` uses case measurement fingerprint, falling back to `CaseId`. Reader attaches it to cases/metrics. Operation measurement fingerprint is preferred for dashboard compare grouping, with legacy fallbacks. |
| Regular `Fingerprint` | Persisted at operation and case level | Same condition inputs as corresponding measurement fingerprint, plus comparison mode | HMAC with the journal's persisted fingerprint salt. Never disclose or reverse engineer it. | Compare history groups by `CompareEvidence.Fingerprint`; SQL versions are separately identified by scoped SQL hashes. |
| Comparison mode | Retained in `execution.comparisonMode`; compared runs also have summary comparison mode | Included in regular fingerprints; omitted from measurement fingerprints | Not sensitive by itself. | Can separate regular compare-condition groups while still allowing case/measurement matching across comparison-mode changes. |
| `Repeat` | Retained in `execution.repeat` and shown in operation conditions UI | Not included in either regular or measurement fingerprint | No omission rule found beyond whole-model conditions JSON size fallback; generic missing conditions remain possible for legacy/invalid JSON. | It can be explained for one operation, but current fingerprint-based grouping does not assert repeat equality. A difference explanation must not claim repeat matched based on fingerprints. |
| `TimeoutSeconds` | Retained in `execution.timeoutSeconds` and shown in operation conditions UI | Not included in either regular or measurement fingerprint | Same as repeat. | Can be explained for one operation; fingerprint matching does not assert timeout equality. |
| `CompareNullability` | Retained in `execution.compareNullability` | Not included in either regular or measurement fingerprint | May be null when not applicable or not recorded; null is not the same as `false`. | Current per-case metric comparer uses measurement fingerprint / case key; this flag is not part of that match. Preserve unknown/absent/null distinctions. |
| Operation kind and warmup count | `execution.operation` and `execution.warmup` are retained in the conditions model | Neither participates in regular or measurement fingerprint payloads | Not sensitive. No field-level omission path; the whole conditions record can be unavailable or invalid, and size fallback removes parameter/setup content rather than these execution fields. | `operation` distinguishes measure from compare and `warmup` describes one operation. Fingerprint matching does not establish equality for either field. |
| Session policy and setup binding | Retained in `execution.sessionPolicy` and `execution.setupBinding` | Both participate in regular and measurement fingerprints, operation and case | Not sensitive. No field-level omission path; whole conditions availability limits still apply. | Fingerprint equality establishes these encoded policy values match, subject to legacy/missing conditions and fallback behavior. Their values can be explained from retained conditions. |
| Case identity | Condition case retains `id`, index, label, optional set name, regular fingerprint, optional measurement fingerprint | Case fingerprint is derived from shared context and that case's varying parameters; measurement case fingerprint removes comparison mode | Case labels may expose values under sensitive storage; otherwise may be generic. Labels are descriptive, not proof of equality. | Dashboard case key is measurement fingerprint, falling back to the raw case ID. When paired, action references map a fingerprint to each side's local raw case ID. |
| SQL version identity | Dashboard version is a scoped SQL hash assigned a readable `vN` label | Not part of condition fingerprint | Hash is an identity only; do not use it to infer SQL or parameter content. SQL is available through version detail when retained. | Versions are grouped within a scope; matching requires one version identity per selected side in the same normalized scope. |
| Subject/database/scope identity | Subject and database are retained in compare evidence; scope is retained in compare evidence and version maps | Subject/scope/hash are not fields in `ConditionsModel` fingerprint payload; target server/database are included there | If subject or database is not directly set, comparer resolves it only from a unique candidate among matching evidence. Ambiguous or missing identity fails closed into generic `conditions differ`. | Scope collisions are isolated. Unassigned compares do not become optimization groups. |
| `omitted` | Conditions payload contains a list of omitted field paths; oversized JSON removal can add paths such as `setup.text` or `fixed.@p.value` | Omission list itself is not in the fingerprint payload | Omission is explicit. If conditions are unavailable or malformed, reader returns null; this differs from a present model with an omitted/null field. | Omission is evidence about unavailable detail, not evidence that the original value was null, empty, or equal to another run. |

## Matching and current generic-message branches

- `OptimizationSummaryBuilder` groups assigned compare evidence by operation `Fingerprint` (with empty string fallback), then constructs scopes and SQL-version rows. The dashboard projection is separate from per-operation `ConditionsModel` detail.
- `OptimizationComparer` returns `same SQL` when each selected side resolves to one hash and both hashes and scopes match. It returns generic `conditions differ` when either side does not resolve to exactly one version in the same scope; subject/database identity cannot be uniquely resolved; or a before/after metric snapshot is unavailable. This label is therefore a fail-closed comparison state, not a diagnosis of which field differs.
- When an eligible direct comparison connects the two SQL hashes, basis is `measured together`; without one, the comparer uses latest snapshots and basis `different runs, same conditions`. This is a matching label based on group/fingerprint membership, not a statement that every retained execution setting (including repeat and timeout) is identical.
- Case pairing uses measurement fingerprint, falling back to case ID. A selected case can consequently map to different local case IDs on either side; action references preserve those mappings.
- Existing UI and Markdown use the same generic text: `conditions differ; SQL diff is the available comparison.` They do not report a field-by-field cause. Existing operation detail separately shows retained setup text, parameters, timeout, repeat, and comparison mode.

## Reopened source locations

- [ConditionsModelBuilder.cs](../../src/SqlHarness.Core/Journal/ConditionsModelBuilder.cs#L19): execution capture, scalar parameter conversion, and exact fingerprint payload builders.
- [ConditionsModelBuilder.cs](../../src/SqlHarness.Core/Journal/ConditionsModelBuilder.cs#L80): materialized list/TVP rows, values, source text, and fingerprint-content capture; [line 193](../../src/SqlHarness.Core/Journal/ConditionsModelBuilder.cs#L193): typed NULL conversion.
- [ActivityJournal.cs](../../src/SqlHarness.Core/Journal/ActivityJournal.cs#L683): sealing, persistence, sensitive-value bounding, and oversized-field omission.
- [ActivityJournal.cs](../../src/SqlHarness.Core/Journal/ActivityJournal.cs#L723): keyed content hashing; [line 760](../../src/SqlHarness.Core/Journal/ActivityJournal.cs#L760): sensitive-value omission at persistence.
- [ConditionsModel.cs](../../src/SqlHarness.Core/Journal/ConditionsModel.cs#L6): serialized conditions schema including repeat, timeout, nullability, and omitted fields.
- [OptimizationSummaryBuilder.cs](../../src/SqlHarness.Dashboard/Optimization/OptimizationSummaryBuilder.cs#L44): operation fingerprint grouping; [line 281](../../src/SqlHarness.Dashboard/Optimization/OptimizationSummaryBuilder.cs#L281): generic group labels when values are not stored.
- [OptimizationComparer.cs](../../src/SqlHarness.Dashboard/Optimization/OptimizationComparer.cs#L7): version/scope identity, generic branches, basis selection, and case pairing.
- [JournalReader.cs](../../src/SqlHarness.Dashboard/JournalReader.cs#L582): mapping conditions and measurement fingerprint to dashboard evidence; [line 1869](../../src/SqlHarness.Dashboard/JournalReader.cs#L1869): unavailable/malformed conditions handling.
- [ComparisonPanel.tsx](../../src/SqlHarness.Dashboard/ui/src/components/optimization/ComparisonPanel.tsx#L20): on-screen basis labels and conditions-differ explanation placement.
- [optimizationMarkdown.ts](../../src/SqlHarness.Dashboard/ui/src/lib/optimizationMarkdown.ts#L3): exact shared conditions-differ copy used in copied Markdown.
- [ConditionsList.tsx](../../src/SqlHarness.Dashboard/ui/src/components/ConditionsList.tsx#L4): operation detail's current `(value not stored)` rendering for both null and undefined/missing; [line 18](../../src/SqlHarness.Dashboard/ui/src/components/ConditionsList.tsx#L18): conditions detail rendering.

## Scope and evidence limits

This is current source behavior, not proof that two runs shared an unrecorded setting. Unknown, omitted, typed NULL, absent JSON property, and empty collections must not be treated as interchangeable inputs in an explanation. There is a current display ambiguity: `ToCondition` materializes a typed null as JSON null, while `ConditionsList.parameterValue` renders both null and undefined/missing as `(value not stored)`. That label alone cannot distinguish a measured NULL from unavailable or missing value evidence. SQL hashes and keyed fingerprint values are not explanation material. No live database, user-home, installation, or runtime journal was inspected.
