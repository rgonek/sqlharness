### Spec Compliance

- ❌ Issues found: The evidence covers the three synthetic cases, required evidence, available measure/plan paths, source safety and size boundaries, and explicit unresolved claims. The new Plan 111 and its in-progress status are absent from the plan index; the repository instructions require updating that index when work lands. See plans/README.md:7 and plans/README.md:15, plans/111-stored-plan-retrieval-study.md:68, and the report's completion claim at .superpowers/sdd/111-stored-plan-retrieval-study/task-1-report.md:21.

### Strengths

- The cases distinguish a measurable current SELECT, a historical SELECT whose original parameters are unavailable, and a stored DML plan; each gives required evidence plus expected and observed source outcomes (plans/111-evidence/task-1-useful-cases.md:13, plans/111-evidence/task-1-useful-cases.md:21, plans/111-evidence/task-1-useful-cases.md:29).
- The boundary analysis accurately describes the 1 MiB inline limit, 16 MiB rooted-file limit, lexical input-root scope and same-user limitation, distiller parser bounds, saved JSON bounds, and unchanged cell cap (plans/111-evidence/task-1-useful-cases.md:39-44). Focused source checks confirm these claims in src/SqlHarness.Mcp/McpLimits.cs:13-25, src/SqlHarness.Mcp/McpInputReader.cs:60-73 and :159-207, src/SqlHarness.Core/PlanDistiller.cs:57, :87-109 and :309-320, and src/SqlHarness.Core/StoredPlanReader.cs:11-21.
- The note separates stored or estimated plans from current runtime measurements and states that no live artifact, database, or loaded binary was inspected (plans/111-evidence/task-1-useful-cases.md:3, :15-19, :23-27, :31-35, :46-50). The inspected artifact and plan paths support its distinction: src/SqlHarness.Core/ArtifactReader.cs:353-367, :429-437; src/SqlHarness.Core/SqlHarnessModule.cs:1561-1587.
- The report records the scope, source revision, drift check, static checks, and that no executable tests or product gate were run (.superpowers/sdd/111-stored-plan-retrieval-study/task-1-report.md:5, .superpowers/sdd/111-stored-plan-retrieval-study/task-1-report.md:11, .superpowers/sdd/111-stored-plan-retrieval-study/task-1-report.md:23-30). No tests were rerun for this review.

### Issues

#### Critical (Must Fix)

- None.

#### Important (Should Fix)

- plans/README.md:15 — Plan 111 has no entry under Pending (and no entry under In progress at plans/README.md:7), although this task added the plan and marked Task 1 complete. The repo contract requires the plan index to track landed work, so the plan is not discoverable through the queue. Add a Plan 111 row under Pending while Tasks 2 and 3 remain open.

#### Minor (Nice to Have)

- .superpowers/sdd/111-stored-plan-retrieval-study/task-1-report.md:11 — The source revision and actual-base bullets are joined by the literal characters \n instead of a line break. This makes the checkout metadata harder to scan; replace the escape with an actual newline.

### Assessment

**Task quality:** Needs fixes

**Reasoning:** The Task 1 research is careful, bounded, and well supported by the named source paths. The missing plan-index entry violates the repository tracking requirement; the report's escaped newline is a small readability issue.

Review report: .superpowers/sdd/111-stored-plan-retrieval-study/task-1-review.md
