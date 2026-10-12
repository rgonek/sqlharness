# Draft: agent-authored session report in the dashboard

Status: **idea under observation — not approved for implementation.** The accepted design refinement
below replaces the earlier six-action lifecycle. No report tool exists yet. Historical evidence-log
entries describe the API considered at the time; their call counts are not the current proposed contract.

## Problem and purpose

An agent interprets SQLHarness evidence in chat, sometimes manually copying numbers or overstating what
was verified. The interpretation should remain linked to its operations in a local report. SQLHarness
renders available metrics from evidence; it does not certify the interpretation.

A useful report must cost less than reconstructing the session. Fitting several sessions to one API,
or collecting consecutive API unchanged entries, is not proof that the API is simple or ready to build.
Evaluate correctness, workflow cost and an operator's need for the feature before implementation.

## Boundaries

- Dashboard stays GET/HEAD-only. Report writes, if implemented, go through MCP into local journal storage.
- Reports are explicitly requested; ordinary SQL operations require no report-write ceremony.
- Store report text as locally sensitive under an explicit policy. Do not publish SQL, values or identifiers.
- Missing evidence, a preflight rejection and a source-code hypothesis remain distinguishable.
- An operation reference proves identity, not correctness, full-domain equivalence or completed plan review.
- Plans 055/058 provide retrievable evidence; 059 provides producer/operation references and honest cost
  coverage. A report must reuse them, not implement a second benchmark/provenance/metrics subsystem.

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

One entry per reviewed session. For each: what the agent would have reported, which actions it would have
called, and what the API lacked or did not need. Record API edits here with their reason.

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
