# SDD ledger — plan: plans/104-stable-local-object-io-aggregation.md

Base: 6d543e509734a374caf882bc8c05ffe5ccaa98b2 (main). Worktree: D:/Dev/sqlharness/.worktrees/plan-104; branch plan-104.
Original checkout dirty README.md, plans/README.md and untracked observation plans 103–108/110–113, preserved. Copied only requested Plan 104 into isolation.
Spec: plan Contract and task assertions.
Routing: Luna high implementers, Luna xhigh task reviewers, Sol low whole-branch reviewer.
Drift scan from planned base: no changes in six named product paths. Predecessor implementations present in main: journal grouping in JournalBenchmarkBuilder, 093 identity selection and 102 linear accounting commits on main; code to be assessed for classification ambiguity before product changes.

| Tasks | Producer/consumer and finding |
|---|---|
| 1 | Tests demand automatic #hex aggregation; STOP forbids guessing explicit #temp identity. Potential fundamental contradiction; preflight review pending. |
| 2 | Shared classifier and collision-safe aggregate representation; requires sound classification evidence, also pending. |
| 3 | Consumer compatibility depends on representation from Task 2; no independent contradiction. |
| 1 / 2 | BenchmarkRunner normalization starts Task 1, shared classifier extracted Task 2. Can stage behavior then extraction if sound classification is possible. |
| 1 / 3 | Regression fixtures feed consumer verification; preserve missing-real-object zero semantics while avoiding invented evidence. |
| 2 / 3 | New metric semantics consumed by projections/artifacts/journal; preserve historical artifacts without reinterpretation. |

Ruling: Assess anonymous-name ambiguity before Task 1 — explicit STOP and positive-identification contract outrank happy-path fixtures — costs delay if existing provenance proves sufficient.
Preflight reviewer: /root/preflight_review, Luna xhigh, read-only. No implementer dispatched yet.
Tasks 1–3: pending reviewed STOP resolution.

STOP confirmed by independent Luna xhigh review: normalized #8hex strings do not establish object kind. Tasks 1–3 unstarted. No implementer, task review, final whole-branch review or implementation gate represented as complete. Ruling: stop under the plan's explicit STOP rather than introduce unplanned provenance collection — plan Contract forbids heuristic attribution — resumption requires reviewed resolution of collection/classification and corresponding task assertions.

## Resumption 2026-10-24
User renewed Plan 104 execution with Luna high implementation, Luna xhigh task review, Sol low whole-branch review, and explicit direction to investigate blockers fully.
Existing ledger and tracked preflight reviewed before dispatch. No task completion exists; no repeated implementation.
Original main checkout remains dirty and untouched. Reused existing plan-104 worktree, initially clean at f18ff03b.
Ruling: merge current main ab46345a into isolated plan-104 before further work — consume current 103/106/108 contracts without touching main — costs a feature-branch merge commit if unnecessary.
Feature synchronization committed at 160067bab0337f98e7db0d054511e4fba0ff963c; no conflicts. Product base for new work: ab46345a2bfc2c89db4646c33895edf11ceede5a.
Fresh Luna high blocker investigation: /root/resolve_104, proposal resolution-proposal.md. No positive classification found; catalog suffix behavior does not prove IO output identity. Typed fields with no runtime producer would not fulfill median repair.
Fresh Luna xhigh reviewed-resolution investigation: /root/resolution_review; evaluating conservative ambiguity alternatives before any product implementation.
Baseline: dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:IsTestProject=true --filter "BenchmarkRunTests|BenchmarkSummaryTests" -v minimal: exit 0, 30 passed, 0 failed/skipped. Log D:/temp/plan104-baseline.log. Build emitted existing Vite chunk-size warning. Plain dotnet test without IsTestProject produced no test discovery and is not accepted as evidence.
Resolution review /root/resolution_review (Luna xhigh): STOP confirmed. Approves honest ambiguity policy, rejects no-producer typed scaffold. Current input cannot satisfy generated-name median and explicit #ABCDEF12 preservation simultaneously. Dashboard independently normalizes names too; a safe availability fallback requires a separate broader consumer contract.
Ruling: retain original Plan104 STOP after renewed source-backed review rather than silently replace aggregation with unavailable/observed-only statistics — none of the proposed implementations satisfies the original task assertions — costs deferred median repair and a separately scoped capture investigation.
Task1 executable RED reproduction dispatched to /root/task1_reproduction (Luna high), source unchanged, evidence-only commit requested. Task1 remains pending/blocked rather than marked complete. Tasks2–3 cannot proceed without positive provenance.
Task1 diagnostic evidence commit c0aa2f3a: temporary tests RED36 total31 passed5 failed, original tests restored. Luna xhigh task1_review: bounded diagnostic compliant, no blocking evidence issue; original Task1 BLOCKED/NOT DONE, no GREEN or implementation claim.
Documentation checks at70b8738f: all new relative Markdown links resolve, git diff --check ab46345a HEAD exits0; src/tests identical to ab46345a and working tree source clean; reproduction patch git apply --check exits0. Raw *.log/*.patch whitespace exempted using local evidence .gitattributes to preserve unmodified evidence.
Sol low /root/whole_review: reviewed ab46345a..95b95ad2, APPROVE bounded diagnostic STOP evidence; no blocking findings. Original Tasks1–3 remain BLOCKED/NOT DONE. No original implementation or fallback approval claimed.
Declined-to-judge rulings: actual SQL Server/version capture attribution remains unverified (no live authorization); future ambiguity-reporting implementation remains separate scope (no product diff); original plan completion is explicitly not claimed. These are recorded limits, not dropped findings.
Plan/index updated; raw baseline, RED patch/log, investigation and independent reviews archived under plans/104-evidence. Current delivery documentation-only, scoped checks green. Retain this ledger/workspace because implementation is blocked, not complete. No merge to main/push/install/live DB.

## Experimental continuation 2026-10-24
User explicitly steers continuation to experiments. Authorized local bounded session-only SQL investigation: localhost/tempdb via integrated auth, no application databases or persistent/config changes. Local connectivity verified with server properties only,5s connection/10s command timeout: SQL Server17.0.1145.4 Standard Developer64bit. No credentials output. This newly authorized experiment supersedes prior no-live-acceptance wording only for its own bounded scope, not previous historical results.
Using diagnosing-bugs methodology; existing RED loop is preserved. /root/live_experiments Luna high owns reproducible SQL/runner+raw IO/XML/counterexample matrix in plans/104-evidence/experiments. Max1000rows per case,3repetitions,30s command. Local temporary stored procedure allowed only as session-owned #procedure to test temp object caching. No product changes yet.
Live matrix found actual upstream distinction: table variables emit bare #8hex; explicit #hex temp tables emit internal session suffix, also across repeated local-temp-procedure calls. Existing parser discards this before benchmarks. Luna xhigh experiment_resolution_review accepts resumption with typed producer-derived identity and Unknown legacy fallback. Empirical scope SQLServer17.0.1145.4; docs gap preserved.
Ruling: resume implementation using classification at the SQLServer STATISTICS IO producer before TempTableKey normalization — live matrix refutes the assumption that upstream source lacks all distinguishing evidence — costs wrong classification on an untested engine naming change, mitigated by strict raw signature and Unknown legacy fallback.
Ruling: Task1 fixtures must enter through capture/parsing provenance rather than treating hand-authored bare dictionaries as classified — typed producer provenance is the reviewed contract — costs fixture changes while preserving original numeric assertions.
Experiment runner deviation: intended analysis-only invocation executed a second3-repeat session-only matrix because switch binding failed. Agent stopped all further SQL and will preserve both manifests; max1000rows per case, no persistent objects/config changes. No expansion to operator targets.
Task1 implementation BASE: 9fa1e67dfdc8f92f5bf6ea9d45c892757d592702 (experiment evidence commit may land independently; review range includes actual resulting HEAD).
User steered to primary Microsoft documentation before implementation. research skill applied; /root/primary_source_research Luna high audited Learn, TDS, engine blog and MicrosoftPress. Source note sqlserver-internal-naming-research.md and controller transport note preserved.
Documented: local temporary suffix mechanism, tempdb table-variable backing, procedure temp-object caching, explicit cached temp tables with bare #8hex catalog names, text-only IO3615/INFO transport. Not documented in examined sources: bare8hex active IO is universally table-variable identity, fixed12hex suffix guaranteed for IO across versions.
Luna xhigh experimental review recommends exact ProductVersion17.0.1145.4 recognition only after primary-source review. This is a research proposal, not implemented runtime policy.
Ruling: do not silently narrow the general Plan104 implementation to a hard-coded17.0.1145.4 allowlist — a single-build experiment is valuable but does not justify substituting a patch-specific product contract for the assigned plan — costs delayed implementation while source-backed recognition policy remains unresolved.
Earlier provisional resumption ruling is superseded to that extent: upstream raw names DO carry tested distinctions, but a general positive-classification contract is not established. Product remains unchanged; no Task1 GREEN or Tasks2–3 completion. Current user request for source investigation is fulfilled with linked primary facts and explicit empirical boundary.
Experiments commit b7274204:7case3repeat matrix plus accidental duplicate3repeat matrix, raw2captures/actualplans/manifests retained; no further SQL run. Separate scripts/evidence do not claim final product gate or full UAT.
