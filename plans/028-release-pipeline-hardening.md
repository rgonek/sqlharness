# Plan 028: Releases carry their real version, only ship from a commit that passes the CI gate, and run with least privilege

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat 5280f78..HEAD -- .github/ Directory.Build.props tests/SqlHarness.Tests/ReleaseWorkflowTests.cs README.md`
> Inspect changes against the current source and adapt implementation details
> within the authorized intent. A changed file or obsolete excerpt alone is
> not a STOP condition. Stop for an incompatible contract, missing required
> proof, or a change that exceeds the approved scope. Record the reconciled base.

## Status

- **Execution**: DONE on `ci/plan-028-release`, 2026-10-13 (`148355b..361e88c`); merge/push pending. Windows and WSL ext4 Linux gates passed (exit 0). Luna high implementation, Luna xhigh task reviews, Sol low whole-branch review and scoped fix review: clean. Hosted release proof remains pending the next operator-triggered tag.

- **Priority**: P2
- **Effort**: S
- **Risk**: LOW
- **Depends on**: plans/016-restore-green-ci.md (the gate must be green before release depends on it). Coordinate with plan 032, which also edits `ci.yml`. Land 028 first, or rebase carefully.
- **Category**: dx / security
- **Planned at**: commit `5280f78`, 2026-10-03

## Why this matters

- **No version stamping.** Nothing sets `<Version>` or passes `-p:Version`. The CLI (`--version`), `capabilities --json` (`version`) and the MCP `serverInfo.version` all report the .NET default `1.0.0` for every release (tags exist: `v0.1.0`…`v0.3.1`). An agent cannot tell a stale binary from a current one, and stale PATH binaries are a documented real problem (plan 015).
- **Releases bypass CI.** `release.yml` triggers on `v*` tags independently of CI and runs `dotnet test` **without** `-warnaserror` or the format gate. On 2026-08-04, CI failed for `v0.3.1` (runs `30914052268`, `30914043359`) while the Release run (`30914056798`) for the same commit published binaries.
- **Over-broad token and mutable dependencies.** `permissions: contents: write` is set for the whole workflow, so the matrix `build` job (which executes NuGet package code and tests) can push to the repo. All actions are pinned by mutable tag (`@v4`). Checkout persists the token in `.git/config`. There is no build-provenance attestation, so users cannot verify where a binary came from (`SHA256SUMS` comes from the same job).

## Current state

- `.github/workflows/release.yml` (68 lines): `on: push: tags: ["v*"]`, top-level `permissions: contents: write`, job `build` (matrix windows-latest/win-x64, ubuntu-latest/linux-x64, macos-14/osx-arm64: checkout, setup-dotnet with `global-json-file`, `dotnet test -c Release`, `dotnet publish src/SqlHarness.Cli -c Release -r <rid> --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o out/<rid>`, archive, `upload-artifact@v4`), and job `release` (`needs: build`, download-artifact, `sha256sum sqlharness-* > SHA256SUMS`, `gh release create "$GITHUB_REF_NAME" artifacts/* --generate-notes --title "$GITHUB_REF_NAME" --repo "$GITHUB_REPOSITORY"` with `GH_TOKEN: ${{ github.token }}`).
- `.github/workflows/ci.yml` (18 lines): `on: push, pull_request`, a single `build` job on ubuntu-latest (restore, build `-warnaserror`, test, format verify). No `permissions:` block.
- `tests/SqlHarness.Tests/ReleaseWorkflowTests.cs` **pins** the release workflow text. Any edit must keep these passing:
  - the `release:` job has **no** `actions/checkout` step;
  - it runs `gh release create "$GITHUB_REF_NAME" artifacts/* --generate-notes --title "$GITHUB_REF_NAME" --repo "$GITHUB_REPOSITORY"`;
  - the build matrix pairs `os: windows-latest` + `rid: win-x64`, `ubuntu-latest` + `linux-x64`, `macos-14` + `osx-arm64` (regex `os:\s*<os>\r?\n\s*rid:\s*<rid>`);
  - the publish step contains `src/SqlHarness.Cli`, `--self-contained true`, `PublishSingleFile=true` and `PublishTrimmed=false`.
- Version readers: `src/SqlHarness.Cli/SqlHarnessCli.cs:25` (`Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)`), `src/SqlHarness.Core/Capabilities.cs:27-29` (also reads `AssemblyInformationalVersionAttribute` as `buildId`), `src/SqlHarness.Mcp/McpHost.cs:22-23`.
- `Directory.Build.props` has no version properties. There is no `.github/dependabot.yml`.

## Commands you will need

| Purpose | Command | Expected on success |
|---|---|---|
| Workflow tests | `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~ReleaseWorkflow"` | all pass |
| Version check (local) | `dotnet build src/SqlHarness.Cli -c Release -p:Version=9.9.9 -o <scratch>` then `<scratch>/sqlharness --version` | `9.9.9` |
| Full gate | `pwsh -NoProfile -File scripts/verify.ps1` | `verify: OK` |
| YAML sanity (if `actionlint` is installed; optional) | `actionlint .github/workflows/*.yml` | no errors |

## Scope

**In scope**: `.github/workflows/release.yml`, `.github/workflows/ci.yml` (permissions and `workflow_call` trigger only), `Directory.Build.props` (dev `VersionPrefix`), `tests/SqlHarness.Tests/ReleaseWorkflowTests.cs` (add assertions; do not weaken existing ones).

**Out of scope**: OS matrix, caching and integration lanes in CI (plans 032/036); code signing with a certificate (needs secrets the operator must provision; record it as a follow-up); changing the RID set.

## Git workflow

Branch `ci/plan-028-release`. Commits per step. Do NOT push, and do not create tags.

## Steps

### Step 1: Version stamping

- `Directory.Build.props`: add `<VersionPrefix>0.0.0</VersionPrefix>` and `<VersionSuffix>dev</VersionSuffix>` so local builds identify as `0.0.0-dev` rather than `1.0.0`. Check what `GetName().Version.ToString(3)` returns with a suffix: `AssemblyVersion` is the numeric `0.0.0.0`, so `--version` prints `0.0.0`. That is acceptable for dev. Capabilities' `buildId` carries the informational version.
- `release.yml` publish step: append `-p:Version=${GITHUB_REF_NAME#v}` (bash), so tag `v0.4.0` → `0.4.0`. The Windows runner uses PowerShell by default for `run:`, so either add `shell: bash` to the publish step or compute the version in a prior step that writes `VERSION` to `$GITHUB_ENV`, then use `-p:Version=${{ env.VERSION }}`.
- Add a test to `ReleaseWorkflowTests.cs`: the publish step passes `-p:Version=`.

**Verify**: the local version check in the table prints `9.9.9`. Workflow tests pass.

### Step 2: Release depends on the CI gate

- `ci.yml`: add `workflow_call:` to `on:` (keep `push` and `pull_request`). Add top-level `permissions: contents: read`.
- `release.yml`: add a first job `gate: uses: ./.github/workflows/ci.yml`, and add `needs: gate` to the `build` job. The `release` job keeps `needs: build`.
- Add a test: `release.yml` contains `uses: ./.github/workflows/ci.yml` and the build job declares `needs: gate`.

**Verify**: workflow tests pass.

### Step 3: Least privilege and credential hygiene

- `release.yml`: top-level `permissions: contents: read`. On the `release` job only: `permissions: contents: write`.
- Every `actions/checkout` step: `with: persist-credentials: false`.
- Add a test: `contents: write` appears only inside the `release:` job.

**Verify**: workflow tests pass. `pwsh -NoProfile -File scripts/verify.ps1` → `verify: OK`.

### Dropped on 2026-10-11

SHA-pinning every action, Dependabot and build-provenance attestation were removed from this plan. For a single-maintainer tool with no external consumers verifying binaries they add review load (every Dependabot PR) without a reader who would check the attestation. Reopen as a separate plan when the release has outside users.

## Test plan

New assertions in `ReleaseWorkflowTests.cs`: the version property, the gate dependency, and job-scoped write permission. Existing contract tests unchanged.

## Done criteria

- [x] `grep -n "contents: write" .github/workflows/release.yml` → only inside the `release:` job
- [x] `grep -n "uses: ./.github/workflows/ci.yml" .github/workflows/release.yml` → match
- [x] Workflow tests and the full gate pass
- [x] `plans/README.md` row updated (note: the first real proof is the next tag build, which the operator triggers)

## STOP conditions

- An existing `ReleaseWorkflowTests` assertion would have to be weakened.
- `workflow_call` reuse makes CI run twice on tag pushes in a way that conflicts with branch protection. Report it; the operator decides.

## Maintenance notes

- After merging, the operator should enable branch protection requiring the CI check, and should cut a release (plan 022's runtime update is the motivating one).
- Authenticode/codesign is deferred: it needs a certificate and secrets the operator controls.

## Execution evidence — 2026-10-13

- Reconciled base: `148355b`; preserved dashboard CI/release stages added since the planned base.
- Version check printed `9.9.9`; release publish uses Bash and the tag-derived Version property.
- Final fix updated observed capability workflow bytes from 7525 to 7529 for the dev suffix; the 8192 budget and call checks remain. Negative workflow tests cover a wrong gate, an additional unhardened checkout and a later write-enabled job.
- Windows: `pwsh ./scripts/verify.ps1` -> `verify: OK`, exit 0; Core 3794 passed, MCP 389 passed/4 skipped.
- Linux ext4: `pwsh ./scripts/verify-linux.ps1` -> `verify-linux: OK`, exit 0; Core 3796 passed, MCP 401 passed/3 skipped. Live database proof is not claimed.
- No tags, push or release publication. Signing, branch protection and the next hosted tag run remain operator follow-ups.
