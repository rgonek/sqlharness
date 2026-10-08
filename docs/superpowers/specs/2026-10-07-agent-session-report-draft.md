# Draft: agent-authored session report in the dashboard

Status: **idea under observation — not approved, not planned.** The operator revisits this draft after each
SQLHarness session (via the `sqlharness-retro` skill) and records whether the proposed API would have fit. Build
only after several sessions have run without API changes; until then, edit the API sections freely and log why.

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
