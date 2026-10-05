# Design: local Linux gate in WSL and a Windows+Linux CI matrix

Date: 2026-10-05
Status: approved in conversation, pending spec review
Branch: `ci/plan-032-matrix`
Relation: implements the matrix/hygiene subset of `plans/032-ci-matrix-and-test-hygiene.md` (its Step 4 minus the Pester step and `.gitattributes`); depends on `plans/016-restore-green-ci.md`, which is merged into `main` at `3606874`.

## Why

Plan 016 made CI green again but proved nothing on Linux: the two tests it repaired
(`McpInputReaderTests.Case_only_sibling_is_rejected_on_case_sensitive_filesystems` and the
`AgentWorkflowTests` compare byte budget) still have never run on a case-sensitive filesystem. Until
a Linux runner executes them, "CI is green" is a claim about Windows. The same applies to every
`if (!OperatingSystem.IsWindows()) return;` guard in the suite: on Linux those tests report a
**pass**, not a skip, so half the platform-specific coverage is invisible.

Two responses, in this order:

1. A local gate that runs the CI stages inside WSL, on a real case-sensitive filesystem, before code
   is pushed. Cheap iteration, no CI minutes.
2. A GitHub Actions matrix over `ubuntu-latest` and `windows-latest`, so the repository is gated on
   both platforms rather than trusting one.

The local gate cannot replace (2): it runs on your hardware and only when you remember to run it.
The matrix cannot replace (1): it costs a push and a wait per iteration.

## Constraints discovered during exploration

These are properties of this machine and this repository, verified before writing the design. Each
one constrains the design; none is a preference.

| Constraint | Consequence |
|---|---|
| WSL2 Ubuntu 24.04.4 exists, passwordless `sudo`, 954 GB free on ext4 | A local Linux gate is feasible. No VM or container work is needed. |
| No .NET SDK is installed in the distro | The gate must provision one. |
| `/mnt/d` (DrvFs) is **case-insensitive**; WSL `/` (ext4) is case-sensitive | The gate clone must live on ext4. Running from `/mnt/d` would hide exactly the filesystem bugs it exists to catch. |
| `%UserProfile%\.wslconfig` caps WSL2 at `memory=4GB`, `processors=4`, `swap=2GB` | Builds may be slow. This file is deliberately hand-tuned and is **not** modified by this work. |
| `global.json` pins SDK `9.0.316` with `rollForward: latestPatch`; the Windows machine resolves to `9.0.318` | The gate reads the version from `global.json`; it never hardcodes it. |
| `.editorconfig` sets no `end_of_line`, and the repo stores LF | `dotnet format --verify-no-changes` is expected to agree on both platforms. Unproven until the gate runs. |
| This repo uses central package management, so no `packages.lock.json` exists, and ever will not | `actions/setup-dotnet`'s built-in cache (`cache: true`) **throws** without a lock file. The workflow must use an explicit `actions/cache` step. |
| `ci.yml` currently runs `dotnet test --no-build` unfiltered | Adding `--filter "FullyQualifiedName!~Integration"` aligns CI with both local gates. The integration tests are opt-in and self-skip without env vars, so behaviour is unchanged. |

## Component 1: `scripts/setup-linux-gate.ps1`

One-time provisioning. Idempotent: a second run reports "already provisioned" and changes nothing.

Steps, in order, failing closed with a concrete remediation message at the first problem:

1. Verify `wsl` exists and at least one WSL2 distro is present. Otherwise stop and print the command
   that fixes it. Do not install a distro automatically.
2. Parse `sdk.version` out of `global.json` at the repository root. No version literal in the script.
3. In the distro: ensure `curl`, `ca-certificates` and `git` are present; install them with `apt-get`
   only if a probe shows they are missing. If `sudo` would prompt for a password, stop and say so
   rather than blocking on input from a non-interactive script.
4. Install the SDK with Microsoft's `dotnet-install.sh` into `~/.dotnet`. User-writable, so no `sudo`
   and no writes to `/usr/share/dotnet`. Re-running the installer for an already-present exact version
   is a no-op. If the download is unreachable, stop and report; do not substitute a different SDK
   version.
5. Put `~/.dotnet` on `PATH` in `~/.bashrc` behind a single guarded marker line, so repeated setup
   runs cannot append duplicates. **This is for interactive convenience only** — Ubuntu's stock
   `~/.bashrc` returns early when the shell is not interactive, so nothing in the gate may depend on
   it. Component 2 exports `PATH` itself in every invocation.
6. Clone the Windows checkout into `~/src/sqlharness-gate` if that directory does not exist. The clone
   is created with `core.autocrlf=false`, so the Linux working tree holds LF — byte-identical to what
   a GitHub runner checks out.

The script writes only inside the WSL distro and its own `~/.bashrc`. It never touches `~/.sqlharness`,
never opens a database connection, and never handles a secret.

## Component 2: `scripts/verify-linux.ps1`

The per-change gate, invoked from the same PowerShell prompt as `scripts/verify.ps1`.

Stage 0 — `==> sync`. Convert the repository root to a WSL path with `wslpath -a`, then inside the
gate clone: `git fetch <windows-repo-path> <branch>`, `git reset --hard FETCH_HEAD`, and
`git clean -fd`. `<branch>` is the branch currently checked out in the Windows repository, resolved
before the first WSL call. `clean -fd` drops stray untracked files but deliberately not `-x`, so
gitignored `bin`/`obj` survive and repeat runs stay incremental.

This is the only write the gate performs, and it targets a disposable clone. The Windows working tree
is read, never modified.

Every WSL invocation exports `PATH="$HOME/.dotnet:$PATH"` inline and prints the resolved
`dotnet --version` before stage 1. Two reasons: the gate must not depend on any shell rc file (Ubuntu's
stock `~/.bashrc` returns early in a non-interactive shell), and a visible SDK version makes a
mismatch against `global.json` obvious instead of silent.

Stages 1–4 are the CI commands, verbatim:

| Stage | Command |
|---|---|
| `restore` | `dotnet restore SqlHarness.sln` |
| `build` | `dotnet build SqlHarness.sln --no-restore -warnaserror` |
| `test` | `dotnet test SqlHarness.sln --no-build --filter "FullyQualifiedName!~Integration" --logger "console;verbosity=normal"` |
| `format` | `dotnet format SqlHarness.sln --no-restore --verify-no-changes` |

`console;verbosity=normal` is a deliberate choice, not a default: it is the only verbosity that prints
per-test skip counts, and the skip list is the entire point of running Linux locally.

Output mirrors `scripts/verify.ps1`: one `==> <stage>` line per stage, then `verify-linux: OK` on
success. On failure it prints which stage failed and exits with that stage's exit code. A sync failure
aborts before any stage runs.

`scripts/verify.ps1` is not modified. The two are siblings: one gate per operating system, invoked
the same way.

## Component 3: `.github/workflows/ci.yml`

```yaml
name: CI

on:
  push:
  pull_request:

concurrency:
  group: ci-${{ github.ref }}
  cancel-in-progress: true

permissions:
  contents: read

jobs:
  build:
    strategy:
      fail-fast: false
      matrix:
        os: [ubuntu-latest, windows-latest]
    runs-on: ${{ matrix.os }}
    timeout-minutes: 30
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json
      - name: Cache NuGet packages
        uses: actions/cache@v4
        with:
          path: ~/.nuget/packages
          key: nuget-${{ runner.os }}-${{ hashFiles('Directory.Packages.props', '**/*.csproj') }}
          restore-keys: nuget-${{ runner.os }}-
      - run: dotnet restore
      - run: dotnet build --no-restore -warnaserror
      - run: dotnet test --no-build --filter "FullyQualifiedName!~Integration" --logger trx --results-directory TestResults
      - name: Upload test results
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: test-results-${{ matrix.os }}
          path: TestResults
      - name: Format check
        if: matrix.os == 'ubuntu-latest'
        run: dotnet format --no-restore --verify-no-changes
```

Rationale for each non-obvious choice:

- **`fail-fast: false`** — a Linux failure must not erase the Windows verdict. You need both.
- **`concurrency` with `cancel-in-progress`** — a newer push supersedes an in-flight run instead of
  paying for both.
- **Explicit `actions/cache`** — required, not stylistic: `setup-dotnet`'s own cache errors out
  without a `packages.lock.json`. The key includes `Directory.Packages.props` because central package
  management means that file, not any lock file, is what actually changes dependencies.
- **trx upload with `if: always()`** — the artifact is how you read which tests skipped on which OS
  without re-running anything. This is what makes the matrix worth its minutes.
- **Format check on ubuntu only** — the Windows leg would verify the same LF-stored content through a
  CRLF checkout, producing noise rather than signal.
- **`permissions: contents: read`** — recommended by `setup-dotnet`'s documentation. Plan 028 also
  wants it; setting it here means 028 finds it present rather than duplicating it.
- **`@v4` action versions** — matches what the file already pins. Version bumps are unrelated churn.
- **`timeout-minutes: 30`** — from plan 032 Step 4.

## Documentation changes

- `README.md`, "Development": name both gates and state what each one can and cannot see. The Windows
  gate cannot see case-sensitivity or POSIX path behaviour; the Linux gate cannot see Windows-only
  process and COM behaviour. Neither replaces CI.
- `AGENTS.md`, "Verify changes": state that a change is not done until both gates pass, which is the
  concrete form of the existing sentence about tests that pass only on Windows.
- `plans/README.md`: row 016 stays as plan 016 left it. Row 032 becomes **PARTIAL**, naming what
  landed (matrix and hygiene) and what did not (Steps 1–3, Pester, `.gitattributes`).

## Verification

1. `pwsh -NoProfile -File scripts/setup-linux-gate.ps1` completes; a second invocation is a no-op.
2. `pwsh -NoProfile -File scripts/verify-linux.ps1` prints `verify-linux: OK`, exits 0, and shows
   per-project pass and skip counts.
3. `pwsh -NoProfile -File scripts/verify.ps1` on Windows is still green on the same commit.
4. Fail-closed proof: break something deliberately inside the gate clone, confirm
   `verify-linux.ps1` fails and names the stage, then discard the clone's state.
5. `actionlint`, if available, reports no errors. A malformed workflow file silently disables all CI,
   so the YAML is reviewed by hand if the linter is absent.
6. `git diff --name-only` lists only `.github/workflows/ci.yml`, `scripts/setup-linux-gate.ps1`,
   `scripts/verify-linux.ps1`, `README.md`, `AGENTS.md`, `plans/README.md`.

## Done criteria

- All six verification items pass.
- Both gates are green on the same commit.
- `plans/README.md` row 032 says PARTIAL and enumerates the remainder.
- Nothing is pushed and no pull request is opened.

## Out of scope

- Plan 028 release gating and provenance.
- Plan 032 Steps 1–3: `[WindowsFact]`/`[UnixFact]` attributes, env-var race removal, publish-directory
  cleanup.
- The Pester step and `.gitattributes` from plan 032 Step 4.
- Integration database jobs (plan 036).
- Any `.wslconfig` change.
- Fixing whatever the first Linux run surfaces. That is a separate decision.

## Risks, stated rather than hidden

- **The first Linux run may find real failures.** Plan 016's two repairs are unproven until then. The
  MCP stdio host tests and `ProcessRunnerTests` have already flaked intermittently on Windows; they may
  behave differently on Linux. Reported, not silently absorbed.
- **Cold caches make the first gate run slow** — a full NuGet restore plus a self-contained
  `linux-x64` publish inside `McpStdioProcessTests`. Later runs reuse `bin`/`obj` and the NuGet cache.
- **4 GB of WSL memory** is the most likely cause of a slow first run. Raising it means editing
  `.wslconfig`, which is out of scope; the remedy, if needed, is a decision for the owner.
- **The Windows leg may exceed 30 minutes** on the self-contained `win-x64` publish. The documented
  remedy is splitting the publish smoke into its own job, which plan 032's maintenance notes already
  anticipate. Not built preemptively.
- **Flaky tests may make either leg intermittently red.** That is genuine signal owned by plan 005, not
  something to paper over with retries in this work.