# Plan 099 — Task 1 report

## Changes

- Added `shadcn` as the exact devDependency `4.22.1` in `src/SqlHarness.Dashboard/ui/package.json` and updated its lockfile.
- No source, `components.json`, runtime dependency, backend, database, or plan/index files changed.
- Commit contains only `src/SqlHarness.Dashboard/ui/package.json` and `src/SqlHarness.Dashboard/ui/package-lock.json`.

## Baseline and verification

- Worktree: `D:\Dev\sqlharness\.worktrees\plan-099`, branch `plan-099`.
- Baseline revision: `45cdd1bfe3a566254c18cd28446e019a1c373e5e`; initial `git status --porcelain=v1` was empty.
- Runtime: root `.nvmrc` is `24.18.0`; `node --version` was `v24.18.0`; `npm --version` was `11.16.0`.
- Before installation, `npm ls shadcn --all` returned `(empty)` with exit 1.
- Read the UI scripts: `check` composes typecheck, Vitest, and oxlint; `build` runs Vite. `components.json` remained the captured Base UI `base-nova`, neutral configuration, with `src/index.css` as its stylesheet.
- Baseline `npm ci` completed successfully (log records 221 packages added). Baseline `npm run build` completed successfully.
- Baseline `npm run check` did not finish within roughly five minutes and was interrupted. The captured log shows the existing `scripts/check.mjs` deprecation warning and repeated `ECONNREFUSED` requests to `::1:3000` and `127.0.0.1:3000`; it does not contain a completed Vitest summary. This is recorded as an incomplete baseline check, not attributed to this dependency change.
- Baseline `npm audit --json` and `npm audit --omit=dev --json` both exited 1 and reported two LOW findings: `monaco-editor` directly and transitive `dompurify`. Both also remain in the production-only audit after installation.
- After installation, `npm run check` was again bounded and interrupted at 120 seconds in Vitest without a summary. `npm run build` completed successfully; Vite emitted its existing large-chunk warning.
- The initial `npm exec` calls through PowerShell's `npm` shim printed npm help/version instead of invoking the package. Re-running through `npm.cmd` used the local executable: `npm.cmd exec -- shadcn --version` printed `4.22.1`, and `npm.cmd exec -- shadcn add --help` exited 0. No package fetch was requested by these local checks.

## Stable version and provenance

- Command: `npm view shadcn dist-tags --json` from the UI root; exit 0. Registry returned `latest: 4.22.1`; `beta`, `rc`, and `canary` pointed to prerelease tags and were excluded.
- Installed exact command: `npm install --save-dev --save-exact shadcn@4.22.1`; exit 0.
- npm registry configuration was `https://registry.npmjs.org/`.
- `npm view shadcn@4.22.1 version dist.tarball dist.integrity --json` returned version `4.22.1`, tarball `https://registry.npmjs.org/shadcn/-/shadcn-4.22.1.tgz`, integrity `sha512-LbB4Lro+b0qSVki/tgL5edtKylEnXe1ppxMJC12d7NHPgaT+eSgJGMQULwP72V7X9tbiUq7xfx3VDAfB7jdGoA==`.
- The lockfile records the exact shadcn version, registry tarball, integrity, and its resolved dependency graph.

## Dependency and audit review

- `npm ls shadcn --all` exited 0 and showed only `shadcn@4.22.1` at the UI root.
- Lock comparison against the baseline revision found 288 new package entries, zero removed entries, and zero version changes to packages already present. The installation reported 289 packages added including shadcn. One existing lock classification changed: `tslib@2.8.1` is now non-optional because the new `recast` CLI dependency requires it; `npm explain tslib` shows this path is under `shadcn`/`@shadcn/registry`.
- No added lock entries set `hasInstallScript`. Some downloaded package metadata contains upstream `prepare` scripts; these registry tarballs did not cause install lifecycle commands to run during this install.
- The full audit changed from 2 LOW / 0 HIGH to 2 LOW / 7 HIGH. New HIGH names are `shadcn`, `@shadcn/registry`, `fast-glob`, `micromatch`, `braces`, `ts-morph`, and `@ts-morph/common`. The audit graph attributes them to the new CLI's registry/glob and AST dependencies; advisories include the `braces` deeply nested-pattern stack-exhaustion denial of service (GHSA-vfj7-8cjw-p6xm). They affect local CLI/registry operations when contributors use the CLI. They are not reported by `npm audit --omit=dev`, which remains at the same two LOW production-tree findings as baseline. This is not evidence that the development CLI is unreachable or risk-free.
- Disposition: retained the requested exact current stable CLI `4.22.1` and documented the new CLI-path HIGH findings for independent task review. No `audit fix`, force downgrade, or override was applied.

## Evidence files

All full logs and copied baseline CSS are in the ignored plan scratch workspace:

- Baseline commands, audits, captured `components.json`, and CSS artifacts: `.superpowers/sdd/099-shadcn-cli-restoration/scratch/baseline/`
- Registry resolution and installed-version commands: `.superpowers/sdd/099-shadcn-cli-restoration/scratch/registry-dist-tags.log` and `.superpowers/sdd/099-shadcn-cli-restoration/scratch/postinstall/`
- CSS copies: `scratch/baseline/css/`; SHA-256 values and byte lengths are in `scratch/baseline/css-sha256.txt`.
- Lock additions/removals/version/metadata comparison and lifecycle review: `scratch/postinstall/lock-added.txt`, `lock-removed.txt`, `lock-version-changes.txt`, `existing-lock-metadata-changes.txt`, `new-package-install-hooks.txt`, and `new-package-lifecycle-metadata.txt`.

Baseline CSS artifacts:

| Artifact | Bytes | SHA-256 |
|---|---:|---|
| `assets/index-D0saNgLL.css` | 75,949 | `57D50072FFCC5FD9B2D54D4724730563DB47BD573A7435B1D90E09DA656B860D` |
| `assets/monaco-DjpsE8to.css` | 99,095 | `797AA85D5570A0EBBB047093F58E9ADC13945078495425B27133073E5B15A9DA` |
| `assets/PlanGraphView-DLioOiRN.css` | 15,413 | `B0CB7BD6FEC5951C9C8F5808821438206604B1629A912C96009AC427F38E7D69` |

## Self-review concerns

- Both baseline and post-install `check` runs stalled in Vitest and did not yield a completed suite result. The post-install run's 120-second timeout and full log are retained. Plan 099's later gate still needs a resolved test-runner environment and complete verification.
- The stable CLI introduces seven HIGH audit findings on its development CLI/registry path. They are recorded with their affected paths and were not suppressed or represented as production-only advisories.
- This task makes no stylesheet import or runtime behavior change; baseline CSS is retained for the following stylesheet task.
