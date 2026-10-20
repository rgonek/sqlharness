# Plan 099 whole-branch review

Base: `45cdd1b`; head: `11d0f68`. Review covers the complete branch against Plan 099 and the revised shadcn design; Plan 100 component adoption is a separate scope. Read-only review; no gate was repeated.

### Strengths

- The package manifest pins stable `shadcn@4.22.1` as a development dependency; the lockfile and retained registry records carry the exact tarball and integrity. Existing package versions were not upgraded (`src/SqlHarness.Dashboard/ui/package.json:49`, `plans/099-verification-evidence.md:24`).
- The stylesheet uses the package export and deletes the vendored copy. The retained build comparison reports byte-identical emitted CSS across all three assets, with representative state selectors and browser checks recorded (`src/SqlHarness.Dashboard/ui/src/index.css:3`, `plans/099-verification-evidence.md:32-37`).
- Audit evidence keeps the nonzero exit codes, distinguishes seven new HIGH findings in the local CLI dependency graph from the unchanged production audit, and states the disposition without claiming unreachability (`plans/099-verification-evidence.md:18-20`).
- The Windows gate evidence names the verified product revision `dd8b9e93`, retained stage logs, passed UI/.NET counts, skipped formatting, and the limits of Linux and live testing (`plans/099-verification-evidence.md:10-14`). The earlier Task 4 durable-log and skipped-format findings were fixed and independently re-reviewed.

### Issues

#### Critical (Must Fix)

None.

#### Important (Should Fix)

1. `README.md:608`: the advertised local CLI preview, diff, and apply commands use `npm exec -- shadcn ...`. In this repository's Windows PowerShell environment, `npm` resolves first to `npm.ps1`; a fresh read-only `npm exec -- shadcn --version` printed `11.16.0` (npm's version) and exited 0, while the recorded `npm.cmd exec -- shadcn --version` printed `4.22.1`. Task 1 already recorded this shim behavior (`plans/099-evidence/task-reports/task-1-report.md:20`). Thus a contributor following the new README on Windows can get a false-success preview and never invoke the installed CLI. Document the tested Windows `npm.cmd exec -- ...` form beside the portable form, or otherwise make the published commands work in PowerShell. Verify the documented read-only preview after the correction.

#### Minor (Nice to Have)

1. `plans/099-evidence/css/task2-build.txt:24` and the other retained raw terminal logs contain trailing whitespace or a blank final line, so full `git diff --check 45cdd1b..11d0f68` exits 1 even though the scoped prose check passes. These are byte-preserved evidence artifacts with no product effect. Keep their provenance; if a future gate requires a clean full diff check, normalize a separate display copy or teach that check to exclude raw logs.

2. `plans/099-verification-evidence.md:36`: reduced-motion emulation still showed the pre-existing 150 ms tooltip enter animation. The emitted CSS was unchanged, so this is not a Plan 099 regression. It remains a useful follow-up for UI accessibility work, without blocking this tooling change.

### Declined to judge

- Plan 100's control inventory, native-to-shadcn conversions, and interaction behavior: this branch intentionally implements only Plan 099 tooling/import/docs.
- Actual safety of applying future registry-generated components: Plan 099 previews and documents review, but no generated component was applied in this branch.
- Linux and live database behavior: the plan requires neither in this run and the evidence explicitly marks both unverified.
- Existing localhost:3000 `ECONNREFUSED` test diagnostics: the Windows gate passed; no evidence connects them to this branch's dependency/import changes. The separate Plan 091 addresses unintended UI test requests.

### Recommendations

Correct the Windows contributor command and retain the full-audit HIGH findings as a visible dependency maintenance item. The raw-log whitespace and reduced-motion observations can be handled outside this branch.

### Assessment

**Ready to merge? With fixes.** The product implementation and verification are sound for Plan 099, but the new contributor workflow fails on the project's demonstrated PowerShell setup. Fix and re-review that documentation before integration; a new product gate is unnecessary if only documentation changes.
