# Plan 099: shadcn CLI Restoration

> **For agentic workers:** Use superpowers:subagent-driven-development or superpowers:executing-plans to implement task-by-task, with independent task and whole-branch review.

**Goal:** Restore supported shadcn tooling and its stylesheet import without the vendored repair workflow.
**Architecture:** Resolve the current stable CLI once, install as a locked devDependency, import its exported CSS and verify emitted behavior. Review upstream changes rather than assume byte identity.
**Tech Stack:** npm, Vite, Tailwind v4, shadcn/base-nova on Base UI.
**Spec:** [revised design](../docs/superpowers/specs/2026-10-18-shadcn-cli-and-primitive-adoption-design.md).
**Status:** IMPLEMENTED and Windows-verified on `plan-099`; the whole-branch review's Important documentation finding is fixed, with final Sol low scoped re-review pending. Not integrated. See the [verification evidence](099-verification-evidence.md).

## Global constraints

- Read plans/README.md, the spec and docs/agents/verification.md. Use an isolated worktree for implementation and preserve unrelated edits.
- UI root below means src/SqlHarness.Dashboard/ui; repository-root commands are explicitly identified.
- Keep Base UI/base-nova/neutral; no blanket init/apply, prerelease adoption, unrelated dependency upgrade or component overwrite.
- Use latest stable at execution start (4.22.1 verified 2026-10-20), then the same installed version for all tasks and the follow-on plan. Record lockfile and registry provenance.
- components/ui is owned editable code. Review deliberate changes; never treat generated code as untouchable.
- No backend, approval, SQL or database behavior changes. No push/merge authorization.
- No fixed audit counts, claims of unreachable vulnerabilities or CSS byte sizes inherited from old notes.

## Review focus

1. CSS resolver succeeds while checked/open/orientation selectors disappear: Task 2 checks emitted representative selectors and browser states.
2. CLI/registry changes overwrite local source: Tasks 1 and 3 preview/diff and record changes.
3. Stable tooling drifts between tasks: Task 1 locks the resolved version; Task 3 uses only the installed executable.
4. A devDependency change hides an audit finding without explaining it: Task 1 captures full and production audit diffs.
5. Stylesheet upgrade changes application behavior: Task 2 covers existing variant consumers in both themes; Task 4 requires final gate and review.

### Task 1: Capture baseline and install current stable CLI

**Files:** UI package.json, package-lock.json.
**Interfaces:** Produces the recorded installed shadcn version for every subsequent CLI invocation; preserves components.json and runtime APIs.

- [x] Record git revision/status, components.json, Node/npm versions and current dependency state. Read existing package scripts and .nvmrc. Do not run init.
- [x] From the UI root run npm ci, npm run check and npm run build; save baseline output and emitted CSS artifacts outside tracked source. Record existing failures honestly rather than silently assigning them to this change.
- [x] Capture npm audit --json and npm audit --omit=dev --json, including their nonzero exit codes when advisories exist.
- [x] Resolve the stable version once using npm view shadcn dist-tags --json. Reject prerelease versions/tags. Install that exact resolved version with npm install --save-dev --save-exact shadcn@<resolved-version>; substitute the actual stable string. Expected today: 4.22.1.
- [x] Check npm ls shadcn and package-lock.json. Review all dependency changes and new lifecycle scripts; do not use audit fix --force. Re-run both audit commands and classify actual new/resolved findings and affected build/runtime paths.
- [x] Run npm exec -- shadcn --version and npm exec -- shadcn add --help. These local commands must use the installed version; stop if local installation is missing instead of fetching a second latest version.
- [x] Run npm run check and npm run build. Obtain independent task review, address findings and commit only this task's files.

### Task 2: Replace the vendored stylesheet with its exported import

**Files:** UI src/index.css; delete UI src/styles/shadcn-tailwind.css.
**Interfaces:** Provides shadcn/tailwind.css via the installed package export without changing theme tokens or app component contracts.

- [x] Inspect the installed package's ./tailwind.css export and compare source with the vendored file. Inventory custom variants, animation definitions and utilities actually used by existing components.
- [x] Replace the vendored import with @import "shadcn/tailwind.css"; delete the vendored file. Keep font/theme/application imports and tokens.
- [x] Build and compare emitted CSS with the Task 1 baseline. Exact equality is useful evidence if observed, not a requirement across a stable version change. Explain differences; reject missing semantic selectors/styles. Do not infer success from a build exit alone.
- [x] Inspect emitted selectors for checked/disabled switches, open dialogs/dropdowns/tooltips, active tabs and separator orientation. Source keyframes unused by any component need not be emitted. Do not add brittle string/byte-count tests merely duplicating upstream CSS.
- [x] Run npm run check. Start dashboard with the documented local UI/backend configuration and fixture data, then inspect existing switches, dialogs, dropdowns, tooltips, tabs and separators in light/dark themes. Check keyboard focus and reduced motion. Record actual results/screenshots and any unverified item.
- [x] Obtain independent task review, address findings and commit.

### Task 3: Establish the maintainable CLI workflow

**Files:** README.md; docs/superpowers/plans/2026-10-08-dashboard-settings-errors-theme.md; plans/071-dashboard-compare-review.md only if its current text contains superseded guidance; plans/README.md.
**Interfaces:** Current instructions describe local CLI preview, review and apply. The original historical evidence remains intact.

- [x] Locate stale instructions with rg -n "shadcn-tailwind|npx shadcn|never edited|stay unchanged" README.md docs plans. Classify current guidance versus historical records.
- [x] Update the current README dashboard contributor paragraph: devDependency, normal CSS import, components.json base-nova and owned/editable component source. Give npm exec -- shadcn add <component> --dry-run, npm exec -- shadcn add <component> --diff, then apply without blanket overwrite after reviewing proposed changes.
- [x] Document explicit upgrades: resolve current stable, review package/lockfile and registry source diffs, run relevant checks. Explain registry output is not frozen by the CLI lockfile.
- [x] Add a brief supersession link to the old 2026-10-08/071 workaround notes where present. Do not rewrite historical results, statuses or old command transcripts. The revised spec explicitly supersedes older immutable-generated/no-style rules.
- [x] Preview npm exec -- shadcn add accordion --dry-run without applying it. Record actual proposed changes; stylesheet or dependency edits are reviewed if proposed, not rejected simply because an old dry-run proposed none.
- [x] Ensure both plans remain discoverable in plans/README.md with accurate dependency/status. Use Plan 099 and Plan 100 with the recorded dependency; do not renumber existing plans.
- [x] Check documentation links and git diff --check.
- [x] Obtain independent task review and commit.

### Task 4: Final verification and evidence

**Files:** create plans/099-verification-evidence.md; update plans/README.md and eventually plans/history.md when implemented.
**Interfaces:** Supplies actual integrated CLI/import predecessor evidence for the adoption plan.

- [x] Perform independent whole-branch review against the revised spec, resolve the Important documentation finding, and retain reviewer/evidence pointers; final Sol low scoped re-review is pending.
- [x] From repository root run just verify on the final product tree; require exit 0. Record revision, command, logs, stage results and any baseline diagnostics. Follow docs/agents/verification.md on reruns and unchanged-tree reuse. Do not run verify-linux unless requested.
- [x] Record npm audit full/production results and their diff from baseline without treating advisory-bearing audit exits as success. New findings require an explicit resolution or documented disposition; no copied two-advisory exception.
- [x] Record version/lockfile, registry preview, CSS differences and browser evidence from Task 2. An unavailable backend/browser fixture is an unverified acceptance item, not a manual pass.
- [x] Write evidence and update index/history accurately. This work can be marked implemented/verified only with required evidence; integration requires separate authorization.
- [x] Check final documentation diff/links and commit requested files.
- [ ] Hand off the adoption plan only after this implementation actually lands on the intended base. Do not push or merge implicitly.
