# Draft: agent-authored session report in the dashboard

Status: **collecting observations — not approved for implementation.** The purpose is to collect
experiences from many SQLHarness sessions, then analyze recurring reporting needs and exceptions.
Only that synthesis should determine whether a shared API and dashboard report are worthwhile.
No report tool exists yet. API sketches below are hypotheses, not a required observation format.

## Collection purpose and workflow

Agents often finish an investigation with a chat report: a results table, what worked, what changed,
conclusions, comments and limitations. Capture what those reports actually contain and what would be
useful to retain in the dashboard, including successful sessions, negative results and partial work.
Do not require reporting friction or fit observations to an existing API.

Append one anonymized entry per reviewed session with:

- Actual report: question, table/report shape, result, agent comments, limitations and open items.
- Evidence and interpretation: sources, references, manual transcription, findings and hypotheses.
- Dashboard usefulness: content worth retaining, missing information and reconstruction effort.
- Cost coverage: observed work/calls, unmeasured costs and separately labeled modeled actions.
- Optional API implications: new need, counterexample, no new need or insufficient evidence.

### Comparable fields (template revision 2026-10-14)

Later synthesis must decide whether one report API covers most sessions, so entries must be comparable.
Entries written before this revision keep their original form; do not rewrite them. From 2026-10-14 each
entry starts with one classification line and adds three fields to the list above:

- **Classification line:** `Kind:` optimization / verification / diagnosis / other · `Outcome:` adopt /
  reject / partial / needsEvidence / pass / fail / none · `Scopes:` count · `System label:` stable label or
  unmapped.
- **Report blocks:** which blocks the final report contained: results table (state its axes, e.g. variant ×
  case), recommendation with trade-offs, decision, questions for a human, limits, follow-up offer, corrections.
- **Content provenance:** approximate counts of report statements by source: journal-backed metrics
  (measure/compare), `query` result rows, artifact sections or files, application code/docs, agent inference.
  Name which ones were transcribed by hand and which decision content has no journal operation at all.
- **Corrections:** claims corrected after delivery: count and cause (stale memory, unlabeled inference,
  misread output, tool defect). Zero is a valid value.

Then add one row to the synthesis table below. Counts are approximate observations, not measurements of
the tool. Keep workload values out.

### Synthesis table (append-only)

| Entry | Kind | Outcome | Scopes | Report blocks | Journal-backed / total numeric statements | Decision content without journal operation | Corrections | API implication |
|---|---|---|---|---|---|---|---|---|
| 2026-10-07 System1 batch optimization | optimization | needsEvidence | not recorded | not recorded | not recorded | not recorded | not recorded | batching |
| 2026-10-07 System2 validation-query optimization | optimization | reject | not recorded | not recorded | not recorded | not recorded | not recorded | batching, cell identity, report-level context |
| 2026-10-08 System2 deployment verification | verification | pass | 5 | not recorded | not recorded | not recorded | not recorded | multi-scope, verification verdicts |
| Evidence: sync-delay diagnosis | diagnosis | not recorded | 5 | not recorded | not recorded | yes (hypothesis) | not recorded | none |
| Evidence: full-import recalculation diagnosis | diagnosis | not recorded | not recorded | not recorded | not recorded | yes (source code) | not recorded | none |
| 2026-10-12 System1 optimization | optimization | none | 1 | not recorded | not recorded | not recorded | not recorded | put/get |
| 2026-10-14 System1 negative optimization | optimization | reject | not recorded | table, limits | not recorded | not recorded | 1 (retro clarification) | none |
| 2026-10-14 point-lookup query (unmapped) | optimization | adopt (marginal) + reject | 1 | variant × case table, recommendation with trade-offs, question for a human, limits, follow-up offer | ~12 / ~15 | yes (trade-offs, cache argument, semantic question) | 3 | none; counterexample to journal-only rendering |
| 2026-10-15 historical query experiment (unmapped) | optimization | partial | 1 | variant × case table, plans, limits, later correction | ~6 / ~9 | yes (source/Git applicability) | 1 | external context required; no API change |
| 2026-10-15 current-source history query (unmapped) | optimization | partial | 1 | variant × case table, source context, outcome, limits, evidence links | ~8 / ~12 | yes (calling conditions, candidate rationale, deferred implementation) | 1 clarification | external context and review coverage; no API decision |
| 2026-10-15 sync-key selection query (unmapped) | optimization | partial | 1 | pre-measurement source context, path × case table, plan comparison, limits, unmeasured side findings, follow-up offer | ~16 / ~24 | yes (source/caller context, Query Store prioritization, unmeasured write-path proposals, deferral) | 1 (unlabeled inference) | external context, unmeasurable side findings, partial evidence; no API decision |
| 2026-10-20 current-source existence-check candidate (unmapped) | optimization | reject | 1 | source context, variant x case table, plan interpretation, limits | not recorded | yes (source applicability, callers, candidate rationale) | 2 clarifications | external context and explicit review coverage; no API decision |
| 2026-10-20 sync-range batch with lookup join (unmapped) | optimization | reject | 1 | pre-measurement source context, case x metric table (baseline only), read-attribution explanation, rejected candidates (1 measured, 2 reasoned), unmeasured out-of-SQL lever, unexplained telemetry gap, evidence links | ~9 / ~22 | yes (selection via historical telemetry plus source match, index and join-shape rejection by reasoning, stopping decision) | 2 (overstated text match; inference presented as measurement) | external context; explicit measured vs inferred labeling; no API decision |
| 2026-10-20 multi-statement read batch, function replaced by ranked CTE (unmapped) | optimization | adopt (proposed, not applied) | 1 | pre-measurement source context with parameter type table, bottleneck explanation, change description, case x metric table (before → after, equality), plan check, limits and uncovered branches, question for a human | ~15 / ~20 | yes (source context, equivalence reasoning for branches without data, untrusted-constraint risk) | 3 clarifications (overstated review coverage x2; unlabeled hypothesis) | external context; explicit review coverage; tool-known hidden work not on agent path; no API decision |
| 2026-10-20 static-list aggregation (unmapped) | optimization | adopt (proposed, not applied) | 1 | source context, variant x metric table, equality, plans, limits, evidence links | ~7 / ~10 | yes (source applicability, callers, rewrite rationale, deferral) | 1 clarification; 1 interim metadata revision | external context and review coverage; no API decision |

Rows for entries before the revision contain only what those entries state; "not recorded" means the
original entry did not record the field, not that the value was zero.

If a report or transcript is unavailable, record that limit; do not invent observed content.
Preserve existing entries verbatim. Append dated corrections or follow-ups with a reference to the
original entry. Design revisions must retain observations even when recorded outside the evidence log.

Dashboard usefulness and token savings are separate questions. Persistence and readability may be
valuable even if reporting adds tokens. Compare the complete workflow with final chat and the existing
journal: discovery/tool schemas, arguments, responses, follow-up reads and duplicated report text.
Design-stage estimates need explicit assumptions and coverage; call counts alone do not prove savings.
Benchmark after implementation unless complete measured bounds already establish a direction under
explicit assumptions. Repeated API unchanged verdicts are not a readiness or approval criterion.

## Recovered founding observations and idea

The following sections are restored from commit c9e069b (before the 2026-10-12 rewrite). They preserve
the first session's observations and early design rationale; references to then-current internals
are historical, not verified claims about the installed tool today.

## Problem

At the end of a measurement session the agent writes a summary into the chat: chosen query, baseline vs
candidate numbers, equivalence, trade-offs, open risks. Observed in the first session (2026-10-07):

- The agent copied numbers by hand from tool JSON and on-disk artifacts, which invites transcription errors.
- One stated hypothesis turned out to be wrong: "LOB reads are probably not counted"; `StatisticsIoParser` does count them.
- The summary disappears into the transcript; it is not linked to the journal rows it is based on.
- The data is sensitive (SQL, plans, parameters), so publishing the summary outside the workstation is wrong.

## Idea

The agent writes **interpretation**; SQLHarness renders **numbers** from the journal. The dashboard shows a report
per investigation: question, attached operations (baseline/candidate/evidence), metrics and deltas pulled from
`operation_metrics` / `operation_table_io` (and per-statement rows, plan 038 W5), the agent's annotations, the
verdict, and open items. A claim that cites no journal evidence is visibly marked.

## Constraints from the existing design

- The dashboard HTTP API stays **GET/HEAD-only** (`2026-10-06-activity-dashboard-design.md`). Writes go through
  MCP into the journal; the dashboard keeps reading. The security model (loopback, token cookie, Host check) is unchanged.
- "Agents send nothing extra" holds for the activity journal. A report is an explicit, opt-in tool call; the
  agent knows about it only through a skill.
- Sensitivity: report text can contain SQL or values. Store it under `journal.storeSensitive`, or always and
  documented as locally sensitive (open question Q3). Render Markdown without raw HTML.

## Interpretation and evidence boundaries

An operation reference proves identity and available evidence, not correctness, full-domain equivalence
or completed plan review. Keep missing evidence, preflight rejections and source-code hypotheses distinct.
Plans 055/058 propose retrievable evidence and 059 proposes producer references and honest cost coverage;
a report design should reuse that work rather than duplicate benchmark, provenance or metrics storage.
Verify plan status and installed behavior before treating any prerequisite as available.
Report writes remain explicitly requested; ordinary SQL operations require no report-write ceremony.

## Design hypotheses and revision history

The six-action v0 sketch is retained below to explain historical entries and recover removed questions.
The 2026-10-12 refinement preferred atomic put/get over that lifecycle; its rationale and evidence remain
valid material for later synthesis. Neither sketch fixes the shape of future observation entries or
authorizes implementation. Historical schema/status assertions need current verification before use.

### Earlier v0 sketch and open questions (restored from c9e069b)

## Proposed API (v0)

### Prerequisite: `operationRef`

Every target-dependent tool response returns an opaque `operationRef` for its journal row. Today only `measure`
and `compare` expose `artifactDirectory`, and `query` exposes nothing a report could reference.

### MCP tool `sqlharness_report`

| action | input | effect |
|---|---|---|
| `create` | `scope`, `title`, `question`, `subject?` (e.g. `File.cs:252`) | returns `reportRef`; owned by the MCP session and scope like artifacts |
| `attach` | `reportRef`, `items: [{ operationRef, role, label }]`; role is `baseline`/`candidate`/`evidence`/`context` | attaches a bounded batch; dashboard pulls metrics, deltas and equivalence from the journal |
| `annotate` | `reportRef`, `items: [{ target, kind, confidence, text }]`; target is report / operation / matrix cell / statement / table / operator; kind is `finding`/`hypothesis`/`decision`/`risk` | a bounded batch; `hypothesis` renders distinctly from `finding`; report targets need no operationRef |
| `claim` | `reportRef`, `items: [{ text, evidence: [{ operationRef, cellIndex?, metric }] }]` (`metric` like `candidate.logicalReads.median`) | a bounded batch; dashboard resolves journal values for the selected cell and flags missing evidence |
| `conclude` | `reportRef`, `verdict` (`adopt`/`reject`/`needsEvidence`), `summary`, `openItems[]` | |
| `get` | `reportRef` | read back for the agent (phase 2: includes operator comments) |

Accepted draft refinements from the second session: batch attach/annotate/claim to avoid one call per row or
annotation. Arrays must be bounded; exact item/text limits and atomicity remain to be settled before implementation.
For matrix evidence, `cellIndex` is a nonnegative index into the attached operation, not a parameter value; require
it for cell metrics and reject nonexistent cells. Operation-level evidence omits it. Annotation targets use the
same operation/cell identity when relevant. A report-level annotation can describe a preflight rejection that has
no journal operation: render it as agent-reported context, not journal-backed execution evidence.

### Journal

`reports(id, session_id, scope_json, title, question, subject, verdict, summary, created_at, updated_at)`,
`report_items(report_id, operation_id, role, label)`, `report_annotations(report_id, target_json, kind,
confidence, text, created_at)`, `report_claims(report_id, text, evidence_json)`, plus `operation_statements` from
plan 038 W5.

### Dashboard

`GET /api/reports?session=&from=&to=&cursor=`, `GET /api/reports/{id}` (report joined with live metrics), a
Reports list and a report detail view reusing the compare side-by-side component.

### Phase 2 (deliberately later)

Operator comments in the dashboard flowing back to the agent through `get`. This needs the first non-GET
endpoint (`POST /api/reports/{id}/comments`) with CSRF protection (SameSite=Strict plus a custom header), which
reopens the dashboard threat model.

## Open questions

- Q1. Is one report per investigation right, or one per session with several questions?
- Q2. Should `claim` metric paths be a closed vocabulary (validated) or free strings (best-effort)?
- Q3. Where does report text sit relative to `storeSensitive`?
- Q4. Token cost: how many extra MCP calls per session are acceptable (budget guess: ≤ 6)?
- Q5. Export: Markdown export of a report for a ticket or PR, with SQL stripped?

### 2026-10-12 refinement (retained)

The following design sections retain the later put/get proposal. This was an accepted draft refinement,
not implementation approval; earlier modeled workflows remain historical observations.

## Proposed API (v1 draft): atomic document, optional read

### Prerequisite: stable operationRef

Every new top-level operation has an opaque operationRef even when journaling is disabled. New saved
artifacts retain producerOperationRef (plan 059). Report evidence can resolve only available journal data:
a reference without a retained row is unavailable evidence, not a reference the agent may invent.

### MCP tool sqlharness_report

| action | input | effect |
|---|---|---|
| put | reportKey, scopes, document | validates and atomically creates/replaces one bounded report; returns reportRef, revision and evidence-resolution diagnostics |
| get | reportRef | optional read of one authorized report and resolved evidence |

document contains title, question, optional subject, operation items, annotations, claims, verdict,
summary and openItems. One write replaces create/attach/annotate/claim/conclude. An operator who needs
only a final report makes one call. get is optional; put must return validation/commit outcome without
requiring read-back to discover whether the save succeeded.

### Atomicity and idempotency

- reportKey is a bounded validated key, scoped to the authorized operator/report namespace.
  Repeating an identical put creates neither another report nor another revision.
- Changed content for the same key replaces the document atomically; no partially attached/concluded
  report is visible. Rejecting a reference or payload leaves the previous document unchanged.
- scopes is a bounded fixed set established by the first successful put. Each operation item must belong
  to one of those authorized scopes. A subsequent put cannot silently widen that set.
- Choose exact list/text/key limits and replacement concurrency semantics before implementation.
  Bounded arrays alone are not enough: validate the total serialized document budget too.
- Return evidence availability and validation diagnostics from put; do not return the whole document
  by default and reintroduce the output cost that the smaller API removes.

### Evidence and claims

Operation items contain operationRef, role (baseline/candidate/evidence/context), and label.
Annotations distinguish finding, hypothesis, decision and risk. They target the report, or an available
operation/cell/statement/operator identity. Report-level context needs no fabricated operation reference.

Metric references use a closed typed selector: operationRef, optional cellIndex/statement selector,
variant and metric. Initial benchmark metrics are logicalReads, cpuMilliseconds and elapsedMilliseconds,
with statistic min/median/max and explicit availability; equivalence is its own outcome selector.
Query row-count references identify resultSetOrdinal. Do not accept arbitrary property-path strings or
run an expression supplied by the agent. Validate that selector fields fit the operation and that referenced
cells/statements actually exist. Missing/disabled metrics remain unavailable; zero is not a substitute.

Claims may attach interpretation text, but numeric values are rendered from the selected evidence.
Successful resolution means the value exists, not that the claim follows from it. Unreferenced claims
remain visibly agent-reported. Retained plan references do not imply completed review.

Verdicts cover optimization (adopt/reject/needsEvidence) and verification (pass/fail/partial).
A diagnosis may use needsEvidence without introducing a new workflow lifecycle.

## Storage and dashboard

Persist one versioned report document plus its authorized scopes and key/revision metadata in a
transaction; derive evidence from the existing journal on read. Do not create separate lifecycle state
for attach/claim/conclude. Query indexes or derived views are implementation choices, not independent
sources of metric truth.

Dashboard may expose GET /api/reports and GET /api/reports/{id}. It renders bounded Markdown without raw
HTML, available metrics, missing-reference diagnostics, agent annotations and the verdict.

Operator comments are outside this proposal. They would require a new write endpoint/threat-model review;
get does not imply that comments or a report-editing product must be built.

## Readiness questions

- Is the linked report used by the operator enough to justify persistent report storage?
- What exact payload/selector limits cover real sessions without encouraging another detail API?
- What report-text retention policy applies when journal.storeSensitive is disabled?
- How are changed-document concurrent puts resolved, and what scope ownership permits later-session reads?
- Does one put plus optional get measurably reduce caller work compared with final chat and existing journal?

Resolve these before an implementation plan. API unchanged entries are observations, not approval.

## Evidence log

Existing entries below are preserved verbatim, including their historical API terminology and modeled
call counts. They do not establish that a report API was executed or that its token cost was measured.
New entries use the collection template above; API conclusions are optional. Append experiences and
corrections rather than rewriting earlier agents' observations.

### 2026-10-07 — System1 batch optimization

- Report shape: an investigation of CPU cost in a batch query; one baseline and several candidate comparisons.
  Candidate variants traded lower CPU for higher reads on sampled inputs. Verdict `needsEvidence`: a larger
  workload remained untested. Application identifiers, workload sizes and measured values are omitted here.
- The original singular API would require substantially more calls than the Q4 budget. Batching attach,
  annotate and claim is needed to keep one investigation within a small number of calls.
- Gaps found: per-statement metrics do not exist in the journal -> `operation_statements` added as a prerequisite;
  `query` operations used as supporting evidence have no `operationRef` -> prerequisite added.
- Not needed: phase 2 comments.

### 2026-10-07 — System2 validation-query optimization

- Report shape: one optimization question; rejected named-type setup, failed parameterized local-temp setup,
  then a successful compare matrix without setup. Verdict `reject`: larger sampled inputs increased total
  logical reads despite lower elapsed time. Open items: setup lifetime and diagnostics, TVP substitution,
  incomplete domain-equivalence and actual-plan validation. No application or persistent database change.
- Evidence basis: errors, rejections and obstructive truncation required artifact retrieval and local report
  extraction. Report numbers should come from attached journal operations/cells, not hand-copied claims.
- Batched actions fit the six-call model: create, attach, annotate, claim, conclude and get once each.
  These are modeled actions, not calls executed in the session; operationRef remains a prerequisite.
- A preflight rejection has no journal row: preserve it as report-level agent context without manufacturing an
  operationRef. Keep the suspected RPC mechanism as a hypothesis. Sampled multiset equality is not full domain
  equivalence; retained plans and operator summaries are not a completed actual-plan review.
- Needed: operationRef, batched actions, explicit matrix cell identity and report-level annotation. Not needed:
  phase 2 comments, SQL/parameter values in payloads or dashboard write endpoints.
- **API edits:** bounded batching for attach/annotate/claim; cellIndex selectors for matrix claims/annotations;
  report-level annotation for failures before journal creation. Storage policy and metric vocabulary remain open.
  This entry is not API unchanged.

### 2026-10-08 — System2 deployment verification across five database scopes

- Report shape: verification rather than optimization. One question spans five isolated database scopes.
  Evidence includes summary checks, an incomplete mapping, a read-only correction simulation and a re-check
  after the operator reran the deployment. Verdict: pass after the correction. Business entities, geographic
  identifiers, ticket identifiers and observed row counts are omitted here.
- Batching reduces the modeled workflow to create, attach, claim, annotate, conclude and get.
- Lacked: one report spans multiple request scopes, but `create` takes a single scope. The verdict vocabulary
  (`adopt`/`reject`/`needsEvidence`) fits optimization, not verification. Claims cite individual `query` cells,
  so operationRef for query remains a prerequisite.
- Not needed: matrix cellIndex, phase 2 comments, plan or operator evidence.
- **API edits:** let a report cover a set of scopes (scope per attached operation, validated against a list fixed
  at create); add verification verdicts `pass`/`fail`/`partial`. This entry is not API unchanged.

### Evidence: sync-delay diagnosis across five isolated database scopes

- Report shape: a diagnosis. Question "why has System2 not received the latest entity dump"; six read-only catalog
  queries over five scopes (job schedule and status per scope) and one row check confirming a stored value. Finding:
  the publish job's last run is days old in four scopes while a sibling job runs nightly. Hypothesis (unverified): an
  in-memory running flag in the job host blocks the job.
- Would have called: create x1, attach x6, annotate x2 (finding, hypothesis), claim x5, conclude x1: 15 calls
  singular, 5 batched.
- Needed: a report spanning several scopes and a non-optimization verdict, both already proposed by the previous
  entry. Not needed: matrix cells, metrics, plans or operator evidence.
- **API unchanged.**

### Evidence: full-import recalculation diagnosis

- Report shape: diagnosis in System2 after a full import from System1. Source code showed that the full-import
  path did not enqueue recalculation, while incremental updates did. Read-only queries confirmed a mismatch
  between imported assignments and derived data. The queue state and the specific job execution remained
  unverified; the code gap alone does not prove what happened in that particular run.
- Investigation calls: capabilities x1, ping x1, query x4. One query was obstructively truncated and one failed
  because the agent used an incorrect object name. Two successful follow-up queries recovered an aggregate
  and a bounded example. No persistent database writes. Retro capability checks are excluded from these counts.
- Modeled report actions: create, attach, annotate, claim, conclude and get once each, six calls with batching.
  These actions were not executed. Attach available operation references; keep source-code conclusions and
  unverified runtime explanations distinct from journal-backed evidence. Claim only supported operation outcomes,
  without copying numerical query findings into metric paths that the journal does not resolve.
- Needed: the existing operationRef prerequisite and finding/hypothesis annotations. Missing operation references
  remain a prerequisite, not references the agent may invent. Non-optimization verdicts are already an observed
  requirement in earlier entries. This diagnosis adds no new API requirement.
- Not needed: matrix cells, benchmark metrics, plans, operator evidence or additional report actions.
- **API unchanged.**

### 2026-10-12 — System1 optimization session: report lifecycle simplification

- Report shape: one scoped investigation with two query candidates, a compare matrix, a second comparison
  and a preflight rejection. No verified product optimization was delivered. Metric overview was read,
  but omitted cases and complete plans were not reviewed; the report must preserve that limitation.
- Investigation tools: capabilities x1, inspect x3, query x2, compare x2, validate x1; nine calls.
  Retro calls are excluded. One matrix response and one long query cell were obstructively truncated;
  validation returned an intentional rejection without a specific detail. No transport switch or mutation.
- Earlier proposal modeled six actions merely because the old API exposed six actions. That does not
  establish their necessity. Current modeled workflow: put x1, get x0 or x1 only if read-back is needed.
  These actions were not executed; operationRef/evidence storage remain prerequisites.
- Needed: atomic idempotent document replacement, stable operation/cell references, typed metric selectors,
  unavailable-evidence diagnostics and distinct agent hypotheses. A put response must make success/failure
  reviewable without returning the whole document or requiring get.
- Not needed: create/attach/annotate/claim/conclude lifecycle mutations, comments, multi-scope data for this
  session, mandatory read-back, a review-approval gate or duplicated metrics storage.
- **API edits:** replace the six-action lifecycle with put/get; make put atomic and idempotent; use closed
  typed evidence selectors; reuse producer references and coverage semantics from plan 059. This entry
  supersedes the earlier retrospective's API unchanged verdict for this session.

### 2026-10-14 — System1 negative optimization result and plan-reading cost

- **Actual report:** One medium-size query, one candidate and three parameter sets. The final chat used a
  before/after table of logical reads and elapsed medians, reported observed equivalence and rejected the
  candidate for a measured latency regression. It linked a local report and stated measurement-order limits.
  No product or database changes. The negative result advanced the investigation.
- **Evidence and interpretation:** Repeated multiset comparison, actual-plan node pages and a separate
  two-direction difference query. Metrics were manually transcribed into chat and a local document.
  Changed operators and estimation discrepancies did not prove the cause of the slowdown. The retrospective
  clarified that all node pages were read but most reads omitted predicates; full node coverage does not
  establish full predicate review. Preserve that clarification with the original conclusion.
- **Dashboard usefulness:** Retain rejected attempts, per-case equivalence/regression, decisions, evidence
  references and limits. A reference establishes available evidence, not completed review or causation.
  The dashboard was not used or inspected; usability gains remain a hypothesis.
- **Observed workflow cost:** capabilities x1, query x4, compare x1, artifact x14: **20 calls**. Artifact work
  was **6 required first pages + 6 required continuation pages + 2 repeated first pages**: 12 necessary reads
  under the existing source-page contract and 2 agent organization errors. Repeats used a different text
  option. Preserve this breakdown in the final decision for [plan 065](../../../plans/065-artifact-plan-pages-and-clipping.md).
  Do not credit the tool with eliminating agent repeats. Subsequent retro/discovery calls are excluded.
- **Cost coverage:** Four exact-text responses had measured UTF-8 bytes. Twelve plan pages were retained as
  parsed JSON; reconstructed size is not exact emitted-byte evidence. Remaining response bytes, inputs,
  tools/list discovery, final chat and retro costs were unmeasured. No complete measured token total or savings
  baseline exists. Byte-based token estimates are not tokenizer measurements. No workload values or output-size
  measurements are published here.
- **Optional API implications:** Retain decisions and limits from negative sessions; no evidence for extra
  report lifecycle actions or API readiness. A six-call plan-reading workflow is hypothetical and conditional
  on all required evidence fitting one response per selection. It was not executed. Call reduction, dashboard
  usefulness and token savings are separate questions. The draft remains collecting observations without API
  changes or implementation approval.

### 2026-10-14 — small point-lookup query: marginal accepted candidate, rejected reshape

System label: unmapped. This entry does not assert identity with the System1/System2 entries above.

- **Actual report:** The agent chose one medium-size count query itself (no user-named target) and measured a
  baseline, then compared two candidates over a five-value matrix: one parameter replacing a settings lookup,
  and an existence-test reshape. The final chat had a variant table (result equivalence per case, per-case
  read deltas, elapsed), a recommendation with trade-offs (the replaced pattern recurs in other queries, so it
  should be changed everywhere or nowhere), a business-semantics question about a filter inside `TOP (1)`,
  limits (an untested code branch and setting value, a sub-resolution timing, local evidence paths) and an offer
  of follow-up work. Outcome: one candidate recommended with a marginal gain, one rejected. No product change.
- **Evidence and interpretation:** Catalog queries (indexes, types, distributions), one measure, two compares.
  Per-table reads in the compare response identified the source of both deltas without artifact reads.
  Measure plan nodes and per-table reads were read from local artifact files outside the tool. Report numbers
  were transcribed by hand from tool responses. The recommendation's trade-offs, the cache claim and the
  semantic question came from application source code, not journal evidence. Corrections after delivery: an
  interim message wrongly said concurrent calls return `busy` (stale agent memory). Two report claims were code
  inferences presented without that label: a removed type conversion and the absence of an extra round trip.
- **Dashboard usefulness:** Retain the variant × case equivalence/read table, the rejected reshape with its
  reason, the recommendation's trade-offs, the open semantic question and the corrections. 9 of 10 regression
  verdicts were `inconclusive` because of whole-millisecond jitter, and a large relative read increase was labelled
  `not-regressed`. Both needed manual explanation in chat ([plan 066](../../../plans/066-regression-resolution-floor-and-cost-thresholds.md)).
  A rendered verdict would have repeated that misleading summary unless it carried the same caveats. The dashboard
  was not used.
- **Observed workflow cost:** capabilities x1, query x6 (one agent SQL error, one re-query after a 512-character
  cell clip), measure x1, compare x2, artifact x0 plus two local file reads: **10 tool calls**. One compare ran
  several times longer than the other with the database idle and moved to the background. Its cause was not
  established. Retro calls (one query, journal and artifact-timestamp reads) are excluded.
- **Cost coverage:** Journal token columns exist for these operations but were not summed for publication.
  Response bytes were only estimated. Inputs, tools/list discovery, final chat and retro were unmeasured.
  No workload values are published here.
- **Optional API implications:** No new report-API need. Counterexample to journal-only rendering: the main
  decision content (trade-offs, a semantic question, a cache argument) has no journal operation. A report needs
  agent-authored context with its source kind (code, data, inference), as the earlier finding/hypothesis
  distinction already suggests. Regression and threshold presentation is a tool-contract issue, not a report-API one.
- **Follow-up 2026-10-14 (same retro, comparable fields added after the template revision):**
  `Kind:` optimization · `Outcome:` adopt (marginal) + reject · `Scopes:` 1 · `System label:` unmapped.
  Report blocks: variant × case table, recommendation with trade-offs, question for a human, limits,
  follow-up offer. Content provenance: about 12 numeric statements came from measure/compare responses,
  2 from `query` rows and 1 from artifact files read outside the tool, all transcribed by hand.
  Decision content without any journal operation came from application code (pattern recurrence, cache
  behavior, filter semantics) and agent inference (recommendation). Corrections: 3 (one from stale agent
  memory, two unlabeled code inferences).

### 2026-10-15 — historical query experiment and later applicability correction

`Kind:` optimization · `Outcome:` partial · `Scopes:` 1 · `System label:` unmapped.
The label is not asserted to match System1/System2 in earlier entries.

- **Actual report:** The agent selected a costly historical query without first verifying
  that its SQL shape still existed in the current application. The initial successful
  report showed a large measured improvement, result equality and an explicit fragment-only
  limitation. A later report, prompted by the operator's question about actual calling
  conditions, established that the historical shape had already been removed from source.
  The experiment remained useful as a tool test, but did not establish an active production
  optimization opportunity. No application or database change was made.
- **Report blocks:** A variant × case table of elapsed medians and logical reads; result
  equivalence and regression verdict; actual-plan interpretation; scope and resolution
  limits; links to local evidence and later documentation; a subsequent applicability
  correction. No final-report question for a human or recommendation to deploy was recorded.
- **Evidence and interpretation:** Repeated multiset comparisons, complete last-measured
  plan node sections for both variants in every case, a two-direction difference query,
  and a later query of historical execution dates. Application source and Git history
  supplied the calling conditions and removal of the measured query shape. Historical
  telemetry in a test copy did not establish current production activity. Plan evidence
  supported the changed access pattern, not an explanation for earlier timeout failures.
- **Content provenance:** Approximately six numeric comparison statements in the results
  table came from journal-backed compare metrics, and three later numeric/date statements
  came from query rows (about nine in total). These are report-statement counts, not a
  count of individual numeric literals. Two plan descriptions came from artifact sections.
  Numbers were manually transcribed into chat and local documents. The main applicability
  decision depended on application source/Git and agent interpretation outside SQLHarness
  journal operations. References to those files are evidence sources, not journal-rendered
  claims. Counts for additional prose statements are not recorded.
- **Corrections:** One confirmed substantive correction after delivery: historical SQL
  had initially been selected without establishing current application relevance. Its
  cause was an agent scope/provenance error, not a tool defect. Separately, the effect of
  changing the client timeout and the necessity of restarting the client were not verified;
  successful later calls finished within the earlier client limit. Do not turn those
  uncertainties into confirmed causes or count them as additional proven tool defects.
- **Dashboard usefulness:** Retain successful experimental evidence alongside the later
  applicability correction, limits and source-code context. A metric-only report would
  miss the most important qualification. The dashboard was not used or assessed; its
  reconstruction effort and usability benefit are not recorded.
- **Observed workflow cost:** capabilities ×2, query ×5, compare ×4, artifact ×6: **17 calls**.
  Comparisons included one SQL timeout, one client tool-call timeout without a SQLHarness
  envelope, one successful three-repeat run and a five-repeat follow-up. The three-repeat
  verdict was inconclusive due to too few runs; the agent had overridden the existing
  five-repeat default. All six artifact reads were useful, complete plan selections with
  no continuation. The empty initial cache check was a valid result, not tool friction.
  Retro/discovery and client-configuration work are excluded from this count.
- **Cost coverage:** Complete emitted bytes, tokenizer counts, tool-discovery/schema input,
  caller arguments, chat, document duplication and retro costs were not measured. No output
  or token savings baseline exists. Modeled report calls were not executed; their count and
  cost are not recorded. No private workload values or identifiers are published here.
- **Optional API implications:** Further evidence that agent-authored source context and
  later corrections must coexist with journal-backed metrics; no new report API operation
  is proposed. Collection remains open and does not establish API readiness. The separate
  [timeout-layer observation](../../observations/mcp-timeout-layers.md) records a possible
  documentation improvement for later decision, not an implementation commitment.
- **Operator decision:** No candidate-selection skill change: this was a tool-version test;
  ordinary future work will specify concrete SQL. Preserve this observation without adding
  a new approval step or runtime assertion that source review occurred.

### 2026-10-15 — current-source history query, reduced reads and deferred implementation

`Kind:` optimization · `Outcome:` partial · `Scopes:` 1 · `System label:` unmapped.
The label is not asserted to match earlier systems. This is a separate reviewed session
from the historical-query experiment above.

- **Actual report:** The agent located a query in current application source, described its
  calling conditions and typed parameter construction before benchmarking, then tested two
  ranking alternatives. The final report presented a candidate with reduced logical reads,
  observed equality and no demonstrated latency improvement. No application or database
  object was changed. The operator subsequently confirmed that the purpose was testing the
  tool and requested retaining the finding in private documentation rather than implementing it.
- **Report blocks:** Variant × case read-count table and relative deltas; source file,
  method, branch and calling conditions; candidate rationale; repeated-result equivalence;
  whole-data difference check; timing-resolution and plan-review limits; local evidence links.
  No deployment recommendation or final-report question for a human was recorded.
- **Evidence and interpretation:** Two matrix comparisons supplied repetitions, execution
  metrics, equality and regression judgments. Catalog/data queries established input types,
  distributions and uniqueness. Two-direction difference queries checked both candidates
  across current data. Three saved-plan reads covered the initial candidate and baseline,
  then the revised candidate, for the largest case only. Application source supplied the
  invocation and SQL/parameter-building contract. Ranking identifiers before fetching payload
  was an agent inference supported by the reviewed plans, not a journal-authored decision.
- **Content provenance:** Approximately eight numeric report statements came from compare
  metrics (four read/delta rows, repetition count, headline reduction and two timing medians),
  three from query rows (result counts/differences), and one from an artifact section
  (estimate versus actual rows). This is about twelve numeric statements, not numeric
  literals; counts are approximate. Additional source-context statements came from application
  code/Git, and candidate/limitation judgments from agent interpretation; their counts are
  not recorded. Numbers were manually transcribed into chat and local documents. Calling
  conditions, rationale and the operator's deferral have no SQLHarness journal operation.
- **Corrections:** One later clarification: full node review covered only the largest case,
  and unreviewed grant/compilation evidence must not be presented as proof that the tool
  cannot collect it. Cause: imprecise distinction between review coverage and surface
  availability, not a demonstrated tool defect. No corrected measured result was recorded.
- **Dashboard usefulness:** Retain the case table, selected and superseded variants,
  current-source context, observed equality, evidence-review coverage and deferred action.
  Journal metrics alone cannot reconstruct those decisions. The dashboard was not used;
  reconstruction effort and usability benefit are not recorded.
- **Observed workflow cost:** capabilities ×1, query ×6, compare ×2, artifact ×3:
  **12 investigation calls**, all successful, with no response truncation. All three artifact
  reads completed without continuation. Discovery, source reads, private documentation and
  retro work are separate and excluded. An overly broad tool-discovery filter printed
  unrelated catalog entries: an agent discovery error, not evidence of an SQLHarness defect.
- **Cost coverage:** Emitted bytes, estimated tokens, artifact volume, input/schema overhead,
  final-chat and document duplication costs were not measured. No savings baseline exists.
  Future report-write calls are modeled only; their count and cost are not recorded.
- **Optional API implications:** Evidence for retaining authored source context, explicit
  review coverage and later decisions alongside metrics. No report API change or readiness
  conclusion follows. The separate [grant/compilation observation](../../observations/plan-memory-and-compilation-evidence.md)
  preserves a potential evidence gap for later analysis, without choosing an implementation.
- **Operator decision:** Record the evidence gap as an observation only; do not create an
  implementation plan. Append this report observation. Product implementation remains deferred.

### 2026-10-15 — sync-key selection query, path-dependent gain and unmeasurable write findings

`Kind:` optimization · `Outcome:` partial · `Scopes:` 1 · `System label:` unmapped.
A separate session from the two 2026-10-15 entries above; the label is not asserted to match them.

- **Actual report:** The operator required source location, calling conditions and the construction of
  the final SQL and parameters to be stated before any measurement, with Query Store used only after
  matching it to current code. The agent used Query Store to prioritize. It set aside an expensive write
  statement as unmeasurable read-only, then selected a read statement whose text matched source exactly.
  Before measuring, it reported file, method, callers, branch selection and typed parameters. The final
  report gave a path × case table, explaining where the gain appears and where it does not. It also gave
  plan-shape comparisons, limits (parameter substitution, unmeasured transaction part, one lost cell,
  timing noise) and three unmeasured side findings (list expansion, an apparently redundant index,
  a missing-index hint judged not useful). It closed with a follow-up offer. The operator then deferred
  implementation, because the purpose was testing the tool, and requested private documentation.
- **Report blocks:** Pre-measurement source and parameter description; path × case results table;
  plan comparison; limits; side findings explicitly labeled unmeasured; evidence file location; follow-up
  offer. No question for a human in the final report.
- **Evidence and interpretation:** Four matrix comparisons (one partial) supplied repetitions,
  per-table IO, equality and regression verdicts. Six artifact reads (statements and plan sections)
  separated plan shape from batch totals. Query rows supplied Query Store text matching, catalog
  indexes, cardinalities and the parameter-type check. Application source supplied the callers and
  SQL composition. The equivalence argument (a semi-join over a primary key cannot duplicate keys) is
  agent inference, supported by observed multiset equality on the measured cases only.
- **Content provenance:** Approximately sixteen numeric statements came from compare metrics
  (table cells, per-table reads), about four from query rows (Query Store and catalog figures) and about
  four from artifact plan nodes (node counts, estimates). That is roughly twenty-four numeric statements,
  not numeric literals; counts are approximate. Numbers were transcribed by hand into chat and private
  documents. Caller inventory, the selection rationale, the write-path proposals and the deferral have
  no journal operation.
- **Corrections:** One. The report said the candidate removed hash operators sized for a large
  estimate (implying a smaller memory grant). This was inferred from estimates, not from grant values.
  During the retro, journal grant fields supported the direction at batch level only. Cause: unlabeled
  inference. See the [follow-up](../../observations/plan-memory-and-compilation-evidence.md#follow-up-2026-10-15).
  The private documentation was corrected.
- **Dashboard usefulness:** Retain the per-path table, the selected shape, the unmeasured side findings
  as labeled hypotheses, and the partial run. The partial comparison's completed cells are absent from
  the journal ([observation](../../observations/partial-compare-journal-coverage.md)), so the dashboard
  could not reconstruct one of the four comparisons. The dashboard was not used.
- **Observed workflow cost:** capabilities ×1, query ×11 (1 safety rejection, 3 Query Store text
  extractions of which 2 were unsuccessful attempts), compare ×4 (1 partial), artifact ×6:
  **22 investigation calls**. Journal-recorded emitted output for these calls totals about 120 KB.
  Retro work (four read-only journal queries, source reads) is excluded.
- **Cost coverage:** Input/schema overhead, final-chat and document duplication, and the share of
  output due to per-instance temporary-object IO entries were not measured. No savings baseline exists.
- **Optional API implications:** More evidence that reports carry authored context (callers, selection
  rationale, deferral) and explicitly unmeasured proposals that no journal operation backs. Partial runs
  need journal coverage before any journal-rendered report could be complete. No report API change or
  readiness conclusion follows.
- **Operator decision:** Record observations for table-valued parameters, write-path and index
  maintenance evidence, statement metrics and partial-run journal coverage. Plan the partial-run
  journal fix ([plan 076](../../../plans/076-partial-matrix-journal-runs.md)); no implementation authorized.

### 2026-10-20 — current-source existence-check candidate rejected

`Kind:` optimization · `Outcome:` reject · `Scopes:` 1 · `System label:` unmapped.
Recorded against repository HEAD `32adaa78`. Status: collecting observations, not approved for
implementation. The system label is not asserted to match earlier entries.

- **Actual report:** The operator required a query from current application source and an account of
  file, method, callers, SQL composition and parameters before measurement. The agent supplied that
  context, compared a correlated existence-check rewrite, and rejected the candidate because repeated
  measurements showed no stable benefit and additional reads in several cases. No application or
  database change was made. Historical query telemetry was not needed.
- **Report blocks:** Source and calling context; variant x case results table; rejection decision;
  interpretation of one selected plan pair; limits concerning dates and tied history rows. No human
  question or follow-up offer appeared in the final investigation report. Retro clarifications followed.
- **Evidence and interpretation:** Three successful compare operations covered nine parameter cases
  with five repetitions each and observed multiset equality. Four query operations supplied target
  confirmation, bounded sample selection and catalog/parameter context. Two artifact reads returned
  complete last-measured plan trees for both variants of one case, without continuation or clipped
  nodes. The plan pair supported an explanation of changed work order in that case only. Formal
  two-direction EXCEPT validation and a complete review of plans, grants and compilation were not
  performed. Equality in measured cases does not establish full domain equivalence.
- **Content provenance:** The results table, repetition count, case count and observed equality came
  from journal-backed compare metrics; target confirmation came from query rows; the access-pattern
  explanation came from artifact nodes and agent interpretation. Source location, caller conditions,
  SQL composition and the rationale for the candidate came from application code outside journal
  operations. Numeric values were manually transcribed into chat. A normalized count of numeric report
  statements and total prose statements is not recorded. The source-applicability and candidate
  rationale decisions cannot be reconstructed from journal metrics alone.
- **Corrections:** Two retro clarifications: plan review covered one case and both variants, rather
  than the entire evidence set; the short reported elapsed range described medians rather than all
  executions. Cause: insufficiently explicit review coverage and aggregation wording in the agent's
  report, not a demonstrated tool defect. Grants and compilation remained unreviewed.
- **Dashboard usefulness:** Preserve the rejected candidate, source context, decision, review limits
  and later clarifications alongside the evidence. Actual dashboard use, usability benefit and
  reconstruction effort are not recorded.
- **Observed workflow cost:** capabilities x1, query x4, compare x3, artifact x2: eleven investigation
  calls. All succeeded; no obstructive SQLHarness response clipping or failed-call retry was observed.
  The negative optimization result was useful evidence, not friction. Retro added two bounded local
  read-only journal queries; repository and discovery reads are separate.
- **Cost coverage:** Journal emission byte counters and estimated-token counters were inspected for
  the nine journaled investigation operations; capabilities and tool-catalog overhead were outside
  that total. These counters do not establish what every model received. Arguments, source reads,
  final-chat duplication and complete workflow token use were not measured. No savings baseline or
  modeled replacement workflow is recorded. Real workload output totals are intentionally omitted.
- **Diagnostic qualification:** Existing aggregate phases were inspected for the relevant operations.
  Connection work dominated the initial successful target check; an available connection event recorded
  success. These records do not identify authentication, network or server as the cause. Detailed
  event payloads were not inspected, and no live query was rerun. Exact installed-build correspondence
  to repository HEAD was not established.
- **Optional API implications:** Another report needs authored source context and explicit evidence
  review coverage beyond journal metrics. This does not select an API or establish readiness or token
  savings. The related [grant/compilation follow-up](../../observations/plan-memory-and-compilation-evidence.md#follow-up-2026-10-20)
  records the review limitation without treating it as a new tool defect.
- **Operator decision:** Record this report observation and the related review-coverage follow-up
  only. No implementation plan, code, skill edit or publication is authorized by this record.

### 2026-10-20 — sync-range batch with lookup join, no better shape found

`Kind:` optimization · `Outcome:` reject · `Scopes:` 1 · `System label:` unmapped.
Recorded against repository HEAD `f928da0d`. Status: collecting observations, not approved for
implementation. The system label is not asserted to match earlier entries.

- **Actual report:** The operator required a query from current application source, with historical
  query telemetry used only after confirming it matched that source, and an account of file, method,
  calling conditions, SQL composition and parameters before measurement. The agent used telemetry to
  rank candidates, chose a static multi-statement range-read batch whose main statement joins a lookup
  table, gave the source context, measured the batch on three parameter sets (two full batches, one
  empty), and compared one equivalent join-predicate variant. It concluded that no rewrite of the SQL
  helps: nearly all work is one clustered-index seek per returned row into the lookup table. It
  dismissed a range-restricted hash/merge shape based on a catalog query showing the per-batch key range
  spans nearly the whole lookup table, and dismissed a narrower covering index because it would have the
  same index depth. It named a data-model change outside SQL as the only real lever, unmeasured. No
  application or database change was made.
- **Report blocks:** Pre-measurement source context; case x metric table (baseline only); explanation of
  where the reads go; list of rejected candidates (one measured, two reasoned only); out-of-SQL lever
  marked unmeasured; an unexplained gap between historical telemetry and measured values; artifact
  identifiers. No human question. Retro corrections followed.
- **Evidence and interpretation:** One measure operation (three parameter sets, seven repetitions)
  and one compare (two sets, seven repetitions, multiset equality) supplied the metrics. One artifact
  `statements` read supplied operator executions per statement. Seven query operations supplied target
  confirmation, telemetry ranking and source-match text, index and table catalog data, index depth and
  key-range distribution; one of them failed with an agent SQL error and was corrected in the next
  call. A telemetry plan document was clipped at the cell limit and abandoned in favour of the measured
  actual plan. Per-table reads were available in the saved artifact `metrics` section but were not read.
- **Content provenance:** Of roughly 22 numeric report statements, about 9 came from journal-backed
  measure/compare metrics, about 7 from `query` rows (telemetry and catalog), about 1 from artifact
  operator counts and about 4 were agent inference (read split between tables, share of work, product of
  seeks and depth). All were transcribed by hand. Candidate selection (telemetry ranking plus source
  match), the index and join-shape rejections, and the decision to stop have no journal operation.
- **Stopping rationale (asked by the operator in the retro):** After one compare the agent stopped on a
  lower-bound argument: the plan already performed roughly one seek per row that needs the lookup, and
  distinct lookup keys almost equalled returned rows, so deduplication, APPLY or reordering could not
  reduce logical reads materially. The hash/merge rejection rested on a catalog query and page-count
  arithmetic, not on a measured variant. The tool did not stop the work; the candidates were left
  unmeasured by agent judgement. Whether a measured hint variant would have been worth its cost as
  evidence is a question for later synthesis.
- **Corrections:** Two, delivered in the retro. (1) The report said the telemetry text matched the
  source exactly; the comparison was visual, without hashing or line-ending normalization. Cause:
  overstated claim. (2) The per-table read split and share of work were presented like measurements but
  were inferred from operator executions and index depth, although measured per-table reads existed in
  the artifact `metrics` section. Cause: unlabeled inference; see the
  [statement-metrics follow-up](../../observations/statement-metrics-and-local-object-noise.md#follow-up-2026-10-20).
- **Dashboard usefulness:** Preserve the source context, the measured baseline, the rejected candidates
  with whether each was measured or reasoned, the stopping rationale and the corrections. Actual dashboard
  use and reconstruction effort are not recorded.
- **Observed workflow cost:** capabilities x1, query x7 (one agent SQL error), measure x1, artifact x1,
  compare x1: eleven investigation calls. One cell clipped (telemetry plan document). No SQLHarness
  rejection, timeout or retry of a tool failure. The negative result was useful evidence, not friction.
- **Cost coverage:** Journal byte and token counters were not inspected. Arguments, source reads,
  discovery overhead, final-chat duplication and complete workflow token use were not measured. No
  savings baseline or modeled workflow is recorded.
- **Diagnostic qualification:** Not applicable; there were no timeouts, failures or unstable calls
  warranting phase review. Phase aggregates were not inspected.
- **Optional API implications:** Another report whose decisive content (source match, candidate
  selection, reasoned rejections, stopping) lies outside journal operations, and where the measured and
  inferred status of each number matters. No API is selected and no readiness or savings follow.
  Related: [telemetry plan clipping](../../observations/query-store-plan-cell-clipping.md).
- **Operator decision:** Record this entry, the statement-metrics follow-up and the telemetry plan
  clipping observation only. No plan, code, skill edit or publication is authorized.

### 2026-10-20 — multi-statement read batch, function replaced by ranked CTE

`Kind:` optimization · `Outcome:` adopt (proposed, not applied) · `Scopes:` 1 · `System label:` unmapped.
Recorded against repository HEAD `a063e75c`. Status: collecting observations, not approved for
implementation. The system label is not asserted to match earlier entries.

- **Actual report:** The operator required a query from current application source, historical query
  telemetry only after confirming it matched that source, and an account of file, method, calling
  conditions, SQL composition and parameters before measurement. The agent selected a static
  multi-statement read batch from source and gave that context, including a parameter type table derived
  from the data-access library's mapping. Two access failures interrupted the work (see diagnostics).
  Telemetry held no trace of the batch, so it was not used. The agent measured the baseline on four
  parameter sets and found that one statement, which joins a multi-statement table-valued function
  computing data for a much larger entity set than needed, dominated CPU. It replaced the function with a
  ranked CTE restricted to the needed rows, compared both batches, and recommended the change without
  applying it to the code. A question about applying it and adding tests ended the report.
- **Report blocks:** Pre-measurement source context; bottleneck explanation; change description; case x
  metric table (before → after, observed equality); plan check of the candidate; limits (branches without
  data, untrusted foreign keys, reads not visible inside the function); question for a human. Retro
  corrections followed.
- **Evidence and interpretation:** One measure (four sets) and one compare (four sets, five repetitions,
  multiset equality) supplied the metrics. Two artifact reads supplied per-statement metrics (partial
  coverage, continuation not followed) and one candidate plan tree. Five successful query operations
  supplied target confirmation, an empty telemetry search, cardinalities, case selection, index and
  constraint catalog data, and an empty edge-case search. Equivalence for two fallback branches rests on
  reading the function's source, not on data. Domain equivalence was not established beyond the measured
  cases; grants and compilation were not reviewed.
- **Content provenance:** Of roughly 20 numeric report statements, about 15 came from journal-backed
  measure/compare metrics, about 3 from artifact sections and about 3 from `query` rows; one ("baseline
  reads probably understated") was agent inference at the time. All were transcribed by hand. Source
  location, caller conditions, parameter mapping, the equivalence argument and the risk assessment have
  no journal operation.
- **Corrections:** Three, delivered in the retro. (1) "Other statements cost about zero" covered two of
  four sets; cause: unfollowed artifact continuation. (2) "Estimates close to actuals" was based on a plan
  in which every actual row count was zero; cause: overstated review coverage. (3) "Reads probably
  understated" was an unlabeled hypothesis; the journal had already recorded the function's hidden work
  (see the [statement-metrics follow-up](../../observations/statement-metrics-and-local-object-noise.md#follow-up-2026-10-20-hidden-work-and-attribution-status-on-the-agent-path)).
  The hypothesis was correct; the cause was evidence missing on the agent path.
- **Dashboard usefulness:** Preserve the source context, the per-case results with equality, the
  uncovered branches and the corrections. The dashboard already shows the hidden-work flag that the agent
  lacked. Actual dashboard use and reconstruction effort are not recorded.
- **Observed workflow cost:** capabilities x1, query x7 (two failed with authentication errors), measure
  x1, artifact x2, compare x1: twelve investigation calls. Both failures had their error text clipped by
  the caller's cell limit ([error message clipping](../../observations/error-message-cell-clipping.md)).
  No tool rejection or retry of a tool failure; one query was re-issued after the operator restored
  access. Empty telemetry and edge-case searches were useful checks, not friction.
- **Cost coverage:** Journal emitted-byte and estimated-token counters were inspected for the eleven
  journaled investigation operations; capabilities and catalog overhead are outside that total. The one
  `statements` artifact read was slightly over half of those bytes. Arguments, source reads, final-chat
  duplication and complete workflow token use were not measured. No savings baseline or modeled
  replacement workflow is recorded. Workload values are intentionally omitted.
- **Diagnostic qualification:** Existing phase aggregates and connection events were inspected for the
  two failed operations and the first two successful ones (bounded local read-only journal access). Both
  failures ended in the connect phase with a failed connection event; the second ran about as long as the
  configured connection timeout. Connect time also dominated the first successful call and was small on
  the next. These records do not identify network, authentication or server as the cause; the operator
  resolved both outside the tool. No live query was rerun.
- **Optional API implications:** Another report whose decisive content (source context, branch
  equivalence argument, risk) lies outside journal operations, and where evidence the tool computes was
  missing from the agent's responses. No API is selected and no readiness or savings follow.
- **Operator decision:** Record this entry, the statement-metrics follow-up and the error-message
  clipping observation only. No plan, code, skill edit or publication is authorized.

### 2026-10-20 — static-list aggregation, lower reads with preserved metadata

Recorded against repository HEAD `6f39a292`. Status: collecting observations, not approved for implementation.

`Kind:` optimization · `Outcome:` adopt (proposed, not applied) · `Scopes:` 1 · `System label:` unmapped.

- **Actual report:** Current-source query selected; source file, method, caller authorization,
  static SQL construction and absence of parameters stated before measurement. Final response
  contains variant x metric table, read-reduction conclusion, equivalence checks, operator
  explanation, uncovered zero-assignment case, candidate file and artifact reference. Product
  implementation deferred because the purpose was testing the tool. No human question or follow-up
  offer.
- **Report blocks:** source context; variant x metric table; rewrite explanation; equality and plan
  review; limit; evidence links; no application changes.
- **Evidence and interpretation:** Two comparisons with five repeats each; four complete
  artifact-plan reads. First comparison used multiset equality and exposed a metadata nullability
  difference without failing default equality. Revised candidate preserved nullability; second
  comparison used ordered strict-nullability equality. Query rows supplied target, schema/index
  context, two-way EXCEPT validation and absence of the uncovered case. Plans supported elimination
  of repeated lookups. No historical telemetry used.
- **Content provenance:** Approximately seven of ten numeric report statements came from compare
  metrics, two from query rows and one from artifact nodes; approximate statement counts exclude
  paths and opaque evidence identifiers. Values manually transcribed. Source location, calling
  conditions, SQL construction, candidate rationale and deferral have no journal operation. Exact
  total prose statement count: not recorded.
- **Corrections:** One retro clarification: complete operator trees were reviewed, but grants and
  compilation were not. Cause: imprecise coverage wording, not a demonstrated tool defect. One
  interim candidate revision corrected metadata nullability; it was disclosed before final reporting
  and is not a retraction of final equality. Initial conservative timing interpretation was updated
  after the second successful comparison.
- **Dashboard usefulness:** Preserve selected source context, deferred adoption, equivalence policy
  revision, result table and uncovered data case. Dashboard was not used. Reconstruction effort and
  actual usability benefit: not recorded.
- **Observed workflow cost:** capabilities x1, query x5 (four successful executions, one rejected
  offline dryRun), compare x2, artifact plan x4: twelve investigation calls. The rejection was
  safety_rejected / ParseError from agent-authored nested CTE syntax; corrected within unchanged
  read-only scope. No clipped SQLHarness response or continuation needed. Retro: two bounded
  read-only journal invocations (schema inspection, then operation phases/cost counters/event
  summaries), plus source/discovery reads; no live SQL rerun.
- **Cost coverage:** Journal emitted-byte and estimated-token counters inspected for eleven
  journaled calls; capabilities excluded. Approximate tokens are byte-derived estimates, not model
  tokenization. Input/catalog overhead, repository reads, final-chat/document duplication and
  whole-workflow cost unmeasured. No savings baseline or modeled replacement.
- **Diagnostic qualification:** Phase aggregates covered the journaled calls. Connection dominated
  initial target confirmation; subsequent connections were smaller. Compare time included execute,
  artifact and other phases. Connection and artifact event summaries showed success; detailed
  payloads not read because aggregates did not warrant causal diagnosis. Parse rejection had only
  other-phase time. Authentication/network/server cause remains unknown. No timing noise attributed
  to infrastructure.
- **Optional API implications:** Authored source applicability and explicit review coverage
  complement journal metrics. No API readiness, implementation decision or savings conclusion
  follows.
- **Operator decision:** Record O1 and O2 only. Private investigation report saved separately; no
  plan, code, skill edit, commit or publication authorized.
- **Related coverage and question:** See the [review-coverage
  follow-up](../../observations/plan-memory-and-compilation-evidence.md#follow-up-2026-10-20--static-list-aggregation).
  Should later synthesis distinguish interim metadata revisions from corrections to a delivered
  final report? Cheapest check: compare this entry with the existing template; no new benchmark.
