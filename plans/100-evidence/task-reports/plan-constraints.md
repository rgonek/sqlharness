## Global constraints and working convention

- Read spec, plans/README.md and docs/agents/verification.md. Implement in an isolated worktree; preserve unrelated work.
- Paths below are relative to src/SqlHarness.Dashboard/ui unless explicitly repository-root paths.
- Keep Base UI/base-nova/neutral. Use installed stable CLI from the predecessor, with reviewed registry diffs; no silent latest refresh or blanket overwrites.
- components/ui is owned code and can receive reviewed changes. Prefer defaults and composition; no exact old-size requirement.
- Preserve labels, domain identities, persisted preferences, resets, approval policy, callbacks and request contracts. No backend/SQL/live writes.
- No generic picker wrapper or form framework just for this migration. Simple Label/id is sufficient; Field is optional where help/error composition warrants it.
- Existing behavior tests are adapted only when markup/API changes. Add focused tests for identified behavioral hazards; no tests merely mirroring trivial Input/Button substitutions.
- Each task: run the named focused Vitest files with npm exec -- vitest run <paths>, run npm run typecheck and npm run lint, obtain independent review and resolve findings, then commit explicit files. Run the whole UI suite at integration points and final just verify; do not label scoped checks a full gate.
- Current inventory is 19 select, 11 details, 3 textarea, 3 checkbox, 4 radio, 2 text input, 9 button and 8 table source sites outside tests/components/ui. Refresh and classify drift before execution; a stale count never licenses silently skipping new controls.
