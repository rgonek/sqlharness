# Plan A/B close-up correction

The operator rejected the visual quality of the first joined control after `bafcd2a5` was installed. Functional checks had passed, but visual acceptance was premature: the small uneven rectangular A/B labels made the control cramped.

Final product candidate: `ecd3f0ae`, same isolated `dashboard-shadcn-fixes` branch/worktree. Changes are limited to VersionPlan usage styles: each label has minimum 80px width, balanced inner spacing and equal circular 16×16px A/B badges with explicit line height. The group is 163×30px for v69/v70 instead of 136.7×28px. No shared control, generated primitive, behavior, API or data changes.

- Actual production build, not an image mock: [before](before.png), [after](after.png), [Side by side / Unified reference](reference.png). Captured at device scale 2 for inspection. [Before geometry](before.json), [after geometry](after.json).
- Images were shown to the operator before another publication; no additional permission flow introduced.
- Ten browser states passed on the final build: keyboard and focus retained, correct selection, full document hash, missing-evidence details, narrow Plan/Usage, operation KPIs, Results and tooltip checks. [Results](qa/results.json).
- Independent scoped review: no findings. Final Windows `just verify` passed on ecd3f0ae: 5 stages / 13m46s; UI 388, Core 4722 (11 skipped), MCP 535 (4 skipped); TypeScript/lint passed, build zero warnings/errors. Format skipped for zero changed C# files. [Logs](gate/ui-check.log). Published locally and ran `sqlharness dashboard --restart`: port 47800 / PID 17804. Installed EXE and 20 served assets match the build. Ten installed browser states passed. [Delivery](delivery.json), [asset hashes](installed-assets.json), [installed browser checks](qa/installed/results.json). No merge or push.

Earlier audit recommendations and their gate are recorded in the [parent report](../verification.md). This follow-up supersedes only the Plan picker spacing and badge presentation.
