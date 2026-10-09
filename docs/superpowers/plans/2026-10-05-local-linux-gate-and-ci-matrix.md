# Local Linux Gate and Windows+Linux CI Matrix Implementation Plan

**Status (2026-10-08):** PARTIAL — Tasks 1, 2, 4 and the Task 3 parity guard are on main (merge de75431, af5cecc, bc6ffa0); the Windows+Linux matrix in `ci.yml` is withheld because of the flaky MCP host tests. The remainder is owned by `plans/032`. Checkboxes below were not maintained during execution.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the repository a local gate that runs the CI stages on a real case-sensitive Linux filesystem, and make GitHub Actions gate on both `ubuntu-latest` and `windows-latest`.

**Architecture:** Two new PowerShell scripts provision and run a disposable Linux checkout inside WSL2's ext4 filesystem — never on `/mnt/d`, which is case-insensitive and would hide the filesystem bugs the gate exists to catch. The Linux gate runs the same four stages CI runs, so the three gates stay in lockstep; a new xunit test enforces that lockstep by reading the three files and comparing them. `ci.yml` becomes a two-OS matrix with an explicit NuGet cache, a timeout, concurrency cancellation, and trx artifacts.

**Tech Stack:** PowerShell 7, WSL2 (Ubuntu 24.04), .NET SDK 9.0.316 pinned by `global.json`, xunit 2.9.3, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-10-05-local-linux-gate-and-ci-matrix-design.md`

## Global Constraints

- **Do not push and do not open a pull request.** Every commit in this plan stays local until the owner says otherwise.
- **The Windows working tree is read, never written, by the Linux gate.** The gate clone at `~/src/sqlharness-gate` is disposable; `scripts/verify-linux.ps1` writes only there.
- **The gate clone must live on WSL ext4, never on `/mnt/d`.** `/mnt/d` is case-insensitive. `scripts/setup-linux-gate.ps1` probes for this and fails closed.
- **No hardcoded SDK version.** Both scripts read `sdk.version` from `global.json`. The literal `9.0.316` must not appear in either script.
- **The gate must not depend on any shell rc file.** Ubuntu's stock `~/.bashrc` returns early in a non-interactive shell, so an rc-file `PATH` would silently not apply. `scripts/verify-linux.ps1` exports `PATH` inline in every stage invocation.
- **The gate must never block on interactive input.** If `sudo` would prompt for a password, stop with a message instead.
- **Neither script may touch `~/.sqlharness`, `targets.json`, any database, or any secret.**
- **Match the existing `scripts/` header style** of `setup-local-postgres.ps1`: `#Requires -Version 7.0`, a `<# .SYNOPSIS / .DESCRIPTION / .NOTES #>` block, `[CmdletBinding()]`, `param()`, `$ErrorActionPreference = 'Stop'`.
- **The four gate stages, in this exact order, with these exact commands**, must be identical in `scripts/verify.ps1`, `scripts/verify-linux.ps1`, and `.github/workflows/ci.yml`:

  | Stage | Command |
  |---|---|
  | restore | `dotnet restore SqlHarness.sln` |
  | build | `dotnet build SqlHarness.sln --no-restore -warnaserror` |
  | test | `dotnet test SqlHarness.sln --no-build --filter "FullyQualifiedName!~Integration"` |
  | format | `dotnet format SqlHarness.sln --no-restore --verify-no-changes` |

  The test stage additionally carries a per-environment logger (`--logger "console;verbosity=normal"` in the Linux gate, `--logger trx` in CI). That flag is deliberately **not** part of the parity contract.

- **Out of scope:** plan 028 release gating, plan 032 Steps 1–3 (`[WindowsFact]`/`[UnixFact]`, env-var races, publish cleanup), the Pester step, `.gitattributes`, integration DB jobs (plan 036), and any `.wslconfig` change.
- **STOP condition:** if the first Linux gate run fails, report the failing stage and the test names and stop. Do not fix product code, test expectations, byte pins, or `.editorconfig` to make it pass. Fixing whatever Linux surfaces is a separate decision.

---

### Task 1: `scripts/setup-linux-gate.ps1` — provision the WSL Linux gate

**Files:**
- Create: `scripts/setup-linux-gate.ps1`
- Create: `tests/SqlHarness.Tests/RepositoryFile.cs`
- Create: `tests/SqlHarness.Tests/SetupLocalLinuxGateScriptTests.cs`

**Interfaces:**
- Consumes: `global.json` at the repository root (`sdk.version`), the Windows repository root path, and a WSL2 distribution named `Ubuntu` (overridable with `-Distro`).
- Produces: a git clone at `$HOME/src/sqlharness-gate` inside the distro, with `core.autocrlf=false`; a .NET SDK at `~/.dotnet` matching `global.json`; a guarded marker line in `~/.bashrc`. `scripts/verify-linux.ps1` (Task 2) depends on all three.

`tests/SqlHarness.Tests/RepositoryFile.cs` is a shared helper for the three new test classes in this plan. It duplicates the private `FindRepositoryFile` already present in `ReleaseWorkflowTests.cs` and `SetupLocalPostgresScriptTests.cs`; those two copies stay untouched, and this one is not reused by them, so the drift guard does not add a fourth copy of the logic to a third place.

- [ ] **Step 1: Write the failing test**

Create `tests/SqlHarness.Tests/RepositoryFile.cs`:

```csharp
namespace SqlHarness.Tests;

internal static class RepositoryFile
{
    public static string Locate(params string[] path)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. path]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate repository file: {string.Join('/', path)}");
    }
}
```

Create `tests/SqlHarness.Tests/SetupLocalLinuxGateScriptTests.cs`:

```csharp
namespace SqlHarness.Tests;

public sealed class SetupLocalLinuxGateScriptTests
{
    private static string Script() => File.ReadAllText(RepositoryFile.Locate("scripts", "setup-linux-gate.ps1"));

    [Fact]
    public void Setup_script_installs_the_sdk_version_pinned_by_global_json_instead_of_hard_coding_one()
    {
        var script = Script();

        Assert.Contains("global.json", script, StringComparison.Ordinal);
        Assert.Contains("ConvertFrom-Json", script, StringComparison.Ordinal);
        Assert.DoesNotContain("9.0.316", script, StringComparison.Ordinal);
        Assert.Contains("dotnet-install.sh", script, StringComparison.Ordinal);
        Assert.Contains("--version \"$sdk_version\"", script, StringComparison.Ordinal);
        Assert.Contains("--install-dir \"$dotnet_dir\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_script_clones_the_gate_onto_a_case_sensitive_filesystem_and_proves_it()
    {
        var script = Script();

        Assert.Contains("src/sqlharness-gate", script, StringComparison.Ordinal);
        Assert.Contains("core.autocrlf false", script, StringComparison.Ordinal);
        Assert.Contains("case-probe", script, StringComparison.Ordinal);
        Assert.Contains("${probe^^}", script, StringComparison.Ordinal);
        Assert.Contains("case-insensitive filesystem", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_script_is_idempotent_and_never_blocks_on_interactive_input()
    {
        var script = Script();

        Assert.Contains("sudo -n true", script, StringComparison.Ordinal);
        Assert.Contains("-d \"$gate_clone/.git\"", script, StringComparison.Ordinal);
        Assert.Contains("grep -qF \"$bashrc_marker\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_script_never_writes_sqlharness_home_or_targets_json()
    {
        var script = Script();

        Assert.DoesNotContain("SQLHARNESS_HOME", script, StringComparison.Ordinal);
        Assert.DoesNotContain("targets.json", script, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet build SqlHarness.sln -warnaserror && dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~SetupLocalLinuxGateScriptTests"`

Expected: build succeeds; 4 tests FAIL with `System.IO.FileNotFoundException: Could not locate repository file: scripts/setup-linux-gate.ps1`.

- [ ] **Step 3: Write the script**

Create `scripts/setup-linux-gate.ps1`:

```powershell
#Requires -Version 7.0
<#
.SYNOPSIS
    Provisions the WSL2 Linux gate that scripts/verify-linux.ps1 runs.

.DESCRIPTION
    Checks that a WSL2 distribution is reachable, installs the .NET SDK version
    pinned by global.json into ~/.dotnet, puts ~/.dotnet on PATH for interactive
    shells, and clones this repository into ~/src/sqlharness-gate.

    The clone lives on the WSL ext4 filesystem on purpose: /mnt/d is
    case-insensitive, so a gate run from there could not observe the
    case-sensitivity failures the gate exists to catch. The script proves the
    filesystem is case-sensitive and fails closed when it is not.

    Idempotent: a second run reports what already exists and changes nothing.

    Fail-closed: never blocks on interactive input, never writes ~/.sqlharness,
    never connects to a database.

.NOTES
    Requires WSL2. Override the distribution with -Distro.
#>
[CmdletBinding()]
param(
    [string]$Distro = 'Ubuntu'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$globalJsonPath = Join-Path $repoRoot 'global.json'
$bashrcMarker = 'sqlharness-linux-gate-path'

if (-not (Get-Command wsl -ErrorAction SilentlyContinue)) {
    throw "wsl was not found. Install WSL2 with 'wsl --install' and re-run this script."
}

& wsl -d $Distro -- true 2>$null
if ($LASTEXITCODE -ne 0) {
    throw "WSL distribution '$Distro' is unavailable. Run 'wsl --list --verbose' to list distributions, then re-run with -Distro <name>."
}

$globalJson = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
$sdkVersion = $globalJson.sdk.version
if ([string]::IsNullOrWhiteSpace($sdkVersion)) {
    throw "global.json does not pin sdk.version."
}

$windowsRepo = (& wsl -d $Distro -- wslpath -a $repoRoot).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($windowsRepo)) {
    throw "Could not translate the repository path '$repoRoot' into a WSL path."
}

$wslHome = (& wsl -d $Distro -- bash -lc 'printf %s "$HOME"').Trim()
$gateClone = "$wslHome/src/sqlharness-gate"

$provision = @'
set -euo pipefail

sdk_version="$1"
bashrc_marker="$2"
windows_repo="$3"
gate_clone="$4"
dotnet_dir="$HOME/.dotnet"

missing=()
for tool in curl git; do
  command -v "$tool" >/dev/null 2>&1 || missing+=("$tool")
done
if [ "${#missing[@]}" -gt 0 ]; then
  if ! sudo -n true 2>/dev/null; then
    echo "sudo needs a password; install these manually and re-run: ${missing[*]}" >&2
    exit 1
  fi
  sudo apt-get update
  sudo apt-get install -y ca-certificates "${missing[@]}"
fi

if [ -x "$dotnet_dir/dotnet" ]; then
  echo "==> sdk already provisioned"
else
  echo "==> sdk $sdk_version"
  installer="$(mktemp)"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$installer"
  bash "$installer" --version "$sdk_version" --install-dir "$dotnet_dir" --no-path
  rm -f "$installer"
fi

if grep -qF "$bashrc_marker" "$HOME/.bashrc" 2>/dev/null; then
  echo "==> path already configured"
else
  echo "==> path"
  {
    echo ""
    echo "# $bashrc_marker"
    echo 'export PATH="$HOME/.dotnet:$PATH"'
  } >> "$HOME/.bashrc"
fi

if [ -d "$gate_clone/.git" ]; then
  echo "==> clone already present at $gate_clone"
else
  echo "==> clone $windows_repo -> $gate_clone"
  mkdir -p "$(dirname "$gate_clone")"
  git clone "$windows_repo" "$gate_clone"
fi
git -C "$gate_clone" config core.autocrlf false

probe="$gate_clone/.sqlharness-case-probe"
: > "$probe"
if [ -e "${probe^^}" ]; then
  rm -f "$probe"
  echo "The gate clone is on a case-insensitive filesystem ($gate_clone). Move it onto the WSL ext4 filesystem." >&2
  exit 1
fi
rm -f "$probe"

"$dotnet_dir/dotnet" --version
'@

$provision | & wsl -d $Distro -- bash -s -- $sdkVersion $bashrcMarker $windowsRepo $gateClone
$exitCode = $LASTEXITCODE
if ($exitCode -ne 0) {
    throw "Linux gate provisioning failed with exit code $exitCode."
}

Write-Host "setup-linux-gate: OK ($gateClone)"
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet build SqlHarness.sln -warnaserror && dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~SetupLocalLinuxGateScriptTests"`

Expected: `Passed! - Failed: 0, Passed: 4`.

- [ ] **Step 5: Run the script for real, then prove it is idempotent**

Run: `pwsh -NoProfile -File scripts/setup-linux-gate.ps1`
Expected: stage lines for sdk / path / clone, then `9.0.316`, then `setup-linux-gate: OK (/home/<user>/src/sqlharness-gate)`. The first run downloads the SDK and clones the repository, so allow 10 minutes.

Run it a second time: `pwsh -NoProfile -File scripts/setup-linux-gate.ps1`
Expected: `==> sdk already provisioned`, `==> path already configured`, `==> clone already present at ...`, then `setup-linux-gate: OK`. Nothing else changes.

If the run reports a case-insensitive filesystem, STOP: the clone landed on the wrong filesystem and the design's core assumption is violated.

- [ ] **Step 6: Commit**

```powershell
git add scripts/setup-linux-gate.ps1 tests/SqlHarness.Tests/RepositoryFile.cs tests/SqlHarness.Tests/SetupLocalLinuxGateScriptTests.cs
git commit -m "ci(032): provision the WSL Linux gate

Installs the global.json SDK into ~/.dotnet and clones the repository to
~/src/sqlharness-gate on case-sensitive ext4, proving the filesystem is
case-sensitive before declaring success."
```

---

### Task 2: `scripts/verify-linux.ps1` — run the CI stages inside WSL

**Files:**
- Create: `scripts/verify-linux.ps1`
- Create: `tests/SqlHarness.Tests/VerifyLinuxScriptTests.cs`

**Interfaces:**
- Consumes: the gate clone, SDK, and `.bashrc` marker produced by Task 1; the current branch name and repository root from the Windows checkout.
- Produces: `verify-linux: OK` with exit 0 when all stages pass; otherwise the failing stage name and that stage's exit code. Documented in `README.md` and `AGENTS.md` in Task 4.

This is the task that produces the first real Linux evidence for plan 016. Its Step 6 may legitimately surface failures; the STOP condition above governs that.

- [ ] **Step 1: Write the failing test**

Create `tests/SqlHarness.Tests/VerifyLinuxScriptTests.cs`:

```csharp
using System.Text.RegularExpressions;

namespace SqlHarness.Tests;

public sealed class VerifyLinuxScriptTests
{
    private static string Script() => File.ReadAllText(RepositoryFile.Locate("scripts", "verify-linux.ps1"));

    [Fact]
    public void Linux_gate_runs_the_four_ci_stages_in_order()
    {
        var stages = Regex.Matches(Script(), @"\bdotnet\s+(restore|build|test|format)\b")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.Equal(["restore", "build", "test", "format"], stages);
    }

    [Fact]
    public void Linux_gate_uses_the_shared_stage_commands()
    {
        var script = Script();

        Assert.Contains("dotnet restore SqlHarness.sln", script, StringComparison.Ordinal);
        Assert.Contains("dotnet build SqlHarness.sln --no-restore -warnaserror", script, StringComparison.Ordinal);
        Assert.Contains(
            "dotnet test SqlHarness.sln --no-build --filter \"FullyQualifiedName!~Integration\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "dotnet format SqlHarness.sln --no-restore --verify-no-changes",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Linux_gate_exports_the_sdk_path_inline_and_never_sources_a_shell_rc_file()
    {
        var script = Script();

        Assert.Contains("export PATH=", script, StringComparison.Ordinal);
        Assert.Contains(".dotnet", script, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?m)^\s*(?:source|\.)\s+\S*(?:bashrc|profile)", script);
    }

    [Fact]
    public void Linux_gate_syncs_the_gate_clone_and_never_writes_this_working_tree()
    {
        var script = Script();

        Assert.Contains("git fetch", script, StringComparison.Ordinal);
        Assert.Contains("git reset --hard FETCH_HEAD", script, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?m)Set-Content|Out-File", script);
        Assert.DoesNotContain("SQLHARNESS_HOME", script, StringComparison.Ordinal);

        var removals = Regex.Matches(script, @"(?m)Remove-Item[^\r\n]*").Select(match => match.Value).ToArray();
        Assert.All(removals, removal => Assert.Contains("$stageFile", removal, StringComparison.Ordinal));
    }

    [Fact]
    public void Linux_gate_delivers_bash_as_an_lf_file_and_never_through_a_pipe()
    {
        var script = Script();

        Assert.Contains("New-TemporaryFile", script, StringComparison.Ordinal);
        Assert.Contains("-replace \"`r`n\", \"`n\"", script, StringComparison.Ordinal);
        Assert.Contains("UTF8Encoding", script, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\|\s*&\s*wsl", script);
    }

    [Fact]
    public void Linux_gate_normalises_windows_path_separators_before_wslpath()
    {
        var script = Script();

        Assert.Contains(@"-replace '\\', '/'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Linux_gate_names_the_failing_stage_and_propagates_its_exit_code()
    {
        var script = Script();

        Assert.Contains("verify-linux: OK", script, StringComparison.Ordinal);
        Assert.Contains("stage '$Name' failed with exit code", script, StringComparison.Ordinal);
        Assert.Matches(@"\$LASTEXITCODE", script);
        Assert.Matches(@"(?m)^\s*exit \$code\s*$", script);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet build SqlHarness.sln -warnaserror && dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~VerifyLinuxScriptTests"`

Expected: build succeeds; 7 tests FAIL with `System.IO.FileNotFoundException: Could not locate repository file: scripts/verify-linux.ps1`.

- [ ] **Step 3: Write the script**

Create `scripts/verify-linux.ps1`:

```powershell
#Requires -Version 7.0
<#
.SYNOPSIS
    Runs the CI gate inside WSL on a case-sensitive Linux filesystem.

.DESCRIPTION
    Syncs the current branch into the disposable gate clone created by
    scripts/setup-linux-gate.ps1, then runs the same four stages CI runs:
    restore, build -warnaserror, test, and format --verify-no-changes.

    The test stage logs skip counts, which is the signal a Windows run cannot
    give. Stops at the first failing stage and exits with that stage's exit
    code, naming the stage.

    Fail-closed: this working tree is read and never written. The gate clone is
    the only thing modified. Never writes ~/.sqlharness and never connects to a
    database.

.NOTES
    Run scripts/setup-linux-gate.ps1 first. Override the distribution with
    -Distro.
#>
[CmdletBinding()]
param(
    [string]$Distro = 'Ubuntu'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$branch = (& git -C $repoRoot rev-parse --abbrev-ref HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($branch)) {
    throw 'Could not determine the current branch.'
}

$windowsRepo = (& wsl -d $Distro -- wslpath -a ($repoRoot -replace '\\', '/')).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($windowsRepo)) {
    throw "Could not translate the repository path '$repoRoot' into a WSL path."
}

$wslHome = (& wsl -d $Distro -- bash -lc 'printf %s "$HOME"').Trim()
$gateClone = "$wslHome/src/sqlharness-gate"

function Invoke-GateStage {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string[]]$Lines,
        [string[]]$Arguments = @()
    )

    Write-Host "==> $Name"

    # PATH is exported here rather than sourced from ~/.bashrc: Ubuntu's stock
    # .bashrc returns early in a non-interactive shell, so an rc-file PATH would
    # silently not apply and the gate would fail for the wrong reason.
    $script = "set -euo pipefail`nexport PATH=`"$HOME/.dotnet:$PATH`"`n" + ($Lines -join "`n")

    # The block is materialised as an LF-only, BOM-less file instead of piped on
    # stdin: PowerShell injects CRLF into native-command stdin, and a trailing CR
    # corrupts the last command's arguments (it made `dotnet --version` fail in
    # scripts/setup-linux-gate.ps1). Windows separators are converted to forward
    # slashes before wslpath for the same class of reason: WSL parses a raw
    # backslash as a shell escape and eats it.
    $stageFile = New-TemporaryFile
    try {
        [System.IO.File]::WriteAllText(
            $stageFile.FullName,
            ($script -replace "`r`n", "`n"),
            [System.Text.UTF8Encoding]::new($false))
        $stageWslPath = (& wsl -d $Distro -- wslpath -a ($stageFile.FullName -replace '\\', '/')).Trim()
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($stageWslPath)) {
            throw "Could not translate the stage script path into a WSL path."
        }

        & wsl -d $Distro -- bash $stageWslPath @Arguments
        $code = $LASTEXITCODE
    }
    finally {
        Remove-Item -LiteralPath $stageFile.FullName -ErrorAction SilentlyContinue
    }

    if ($code -ne 0) {
        Write-Host "verify-linux: stage '$Name' failed with exit code $code."
        exit $code
    }
}

Invoke-GateStage -Name 'sync' -Arguments @($gateClone, $windowsRepo, $branch) -Lines @(
    'cd "$1"',
    'git fetch "$2" "$3"',
    'git reset --hard FETCH_HEAD',
    'git clean -fd'
)

Invoke-GateStage -Name 'sdk' -Arguments @($gateClone) -Lines @(
    'cd "$1"',
    'dotnet --version'
)

Invoke-GateStage -Name 'restore' -Arguments @($gateClone) -Lines @(
    'cd "$1"',
    'dotnet restore SqlHarness.sln'
)

Invoke-GateStage -Name 'build' -Arguments @($gateClone) -Lines @(
    'cd "$1"',
    'dotnet build SqlHarness.sln --no-restore -warnaserror'
)

Invoke-GateStage -Name 'test' -Arguments @($gateClone) -Lines @(
    'cd "$1"',
    'dotnet test SqlHarness.sln --no-build --filter "FullyQualifiedName!~Integration" --logger "console;verbosity=normal"'
)

Invoke-GateStage -Name 'format' -Arguments @($gateClone) -Lines @(
    'cd "$1"',
    'dotnet format SqlHarness.sln --no-restore --verify-no-changes'
)

Write-Host 'verify-linux: OK'
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet build SqlHarness.sln -warnaserror && dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~VerifyLinuxScriptTests"`

Expected: `Passed! - Failed: 0, Passed: 7`.

- [ ] **Step 5: Run the gate — the first real Linux evidence**

Run: `pwsh -NoProfile -File scripts/verify-linux.ps1`

Expected: `==> sync`, `==> sdk` with `9.0.316`, `==> restore`, `==> build` with `0 Warning(s)` / `0 Error(s)`, `==> test` with both projects' pass and skip counts, `==> format`, then `verify-linux: OK`, exit 0.

The first run restores NuGet from cold and publishes a self-contained `linux-x64` in `McpStdioProcessTests`. Allow 20 minutes; do not interrupt it.

**If any stage fails: STOP.** Record the stage, the exit code, and every failing test name, then report. Do not edit product code, test expectations, byte pins, `.editorconfig`, or `global.json` to get green. Fixing what Linux surfaces is a separate decision for the owner.

- [ ] **Step 6: Prove the gate fails closed**

Inside the gate clone, introduce a formatting violation and confirm the gate names the format stage:

```powershell
wsl -d Ubuntu -- bash -lc 'printf "\n\n" >> $HOME/src/sqlharness-gate/src/SqlHarness.Cli/Program.cs'
pwsh -NoProfile -File scripts/verify-linux.ps1
```

Expected: `verify-linux: stage 'format' failed with exit code 2.` and a non-zero process exit code. The next `verify-linux.ps1` run restores the file, because the sync stage does `git reset --hard`.

- [ ] **Step 7: Commit**

```powershell
git add scripts/verify-linux.ps1 tests/SqlHarness.Tests/VerifyLinuxScriptTests.cs
git commit -m "ci(032): run the CI gate on Linux through WSL

Syncs the current branch into the disposable gate clone and runs the same
four stages CI runs, logging test skips that a Windows run cannot show."
```

---

### Task 3: `.github/workflows/ci.yml` — Windows and Linux matrix, and the parity guard

**Files:**
- Modify: `.github/workflows/ci.yml` (whole file, currently 18 lines)
- Create: `tests/SqlHarness.Tests/CiGateParityTests.cs`

**Interfaces:**
- Consumes: the stage commands established by Task 2 and already present in `scripts/verify.ps1` from plan 016.
- Produces: a workflow that gates on both operating systems, and a test that fails when any of the three gates drifts from the others.

The parity test is the point of this task. Plan 016 shipped a gate whose test filter had already drifted from `ci.yml`; nothing noticed. This test makes that class of drift a build failure.

- [ ] **Step 1: Write the failing test**

Create `tests/SqlHarness.Tests/CiGateParityTests.cs`:

```csharp
using System.Text.RegularExpressions;

namespace SqlHarness.Tests;

public sealed class CiGateParityTests
{
    private static readonly string[] ExpectedVerbs = ["restore", "build", "test", "format"];

    private static string Workflow() => File.ReadAllText(RepositoryFile.Locate(".github", "workflows", "ci.yml"));

    private static string StageCommand(string content, string verb)
    {
        var match = Regex.Match(content, @"\bdotnet\s+" + verb + @"\b[^\r\n]*");

        Assert.True(match.Success, $"No 'dotnet {verb}' command was found.");
        return match.Value
            .Replace('\'', '"')
            .Trim()
            .TrimEnd('"', '}', ';', ',')
            .Trim();
    }

    [Theory]
    [InlineData("scripts/verify.ps1")]
    [InlineData("scripts/verify-linux.ps1")]
    [InlineData(".github/workflows/ci.yml")]
    public void Every_gate_runs_the_same_four_stages_in_the_same_order(string path)
    {
        var content = File.ReadAllText(RepositoryFile.Locate(path.Split('/')));
        var verbs = Regex.Matches(content, @"\bdotnet\s+(restore|build|test|format)\b")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.Equal(ExpectedVerbs, verbs);
    }

    [Theory]
    [InlineData("scripts/verify.ps1")]
    [InlineData("scripts/verify-linux.ps1")]
    [InlineData(".github/workflows/ci.yml")]
    public void Every_gate_builds_with_warnaserror_and_formats_with_verify_no_changes(string path)
    {
        var content = File.ReadAllText(RepositoryFile.Locate(path.Split('/')));

        Assert.Contains("-warnaserror", StageCommand(content, "build"), StringComparison.Ordinal);
        Assert.Contains("--no-restore", StageCommand(content, "build"), StringComparison.Ordinal);
        Assert.Contains("--verify-no-changes", StageCommand(content, "format"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("scripts/verify.ps1")]
    [InlineData("scripts/verify-linux.ps1")]
    [InlineData(".github/workflows/ci.yml")]
    public void Every_gate_runs_the_same_non_integration_test_selection(string path)
    {
        var command = StageCommand(File.ReadAllText(RepositoryFile.Locate(path.Split('/'))), "test");

        Assert.Contains("--no-build", command, StringComparison.Ordinal);
        Assert.Contains("--filter", command, StringComparison.Ordinal);
        Assert.Contains("FullyQualifiedName!~Integration", command, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_gates_both_supported_platforms_without_fail_fast()
    {
        var workflow = Workflow();

        Assert.Contains("ubuntu-latest", workflow, StringComparison.Ordinal);
        Assert.Contains("windows-latest", workflow, StringComparison.Ordinal);
        Assert.Contains("fail-fast: false", workflow, StringComparison.Ordinal);
        Assert.Contains("runs-on: ${{ matrix.os }}", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_cancels_superseded_runs_and_bounds_each_job()
    {
        var workflow = Workflow();

        Assert.Contains("concurrency:", workflow, StringComparison.Ordinal);
        Assert.Contains("cancel-in-progress: true", workflow, StringComparison.Ordinal);
        Assert.Contains("timeout-minutes:", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_caches_nuget_without_depending_on_a_lock_file()
    {
        var workflow = Workflow();

        Assert.Contains("actions/cache@", workflow, StringComparison.Ordinal);
        Assert.Contains("~/.nuget/packages", workflow, StringComparison.Ordinal);
        Assert.Contains("hashFiles('Directory.Packages.props'", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("cache: true", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_uploads_test_results_even_when_a_stage_fails()
    {
        var workflow = Workflow();

        Assert.Contains("--logger trx", workflow, StringComparison.Ordinal);
        Assert.Contains("--results-directory TestResults", workflow, StringComparison.Ordinal);
        Assert.Contains("actions/upload-artifact@", workflow, StringComparison.Ordinal);
        Assert.Matches(@"(?s)if:\s*always\(\).*?actions/upload-artifact@", workflow);
    }

    [Fact]
    public void Workflow_checks_formatting_on_linux_only()
    {
        var workflow = Workflow();

        Assert.Matches(
            @"(?s)if:\s*matrix\.os\s*==\s*'ubuntu-latest'.*?dotnet format",
            workflow);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet build SqlHarness.sln -warnaserror && dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~CiGateParityTests"`

Expected: build succeeds; 6 tests FAIL. `Every_gate_runs_the_same_non_integration_test_selection` fails for `.github/workflows/ci.yml` only (it has no `--filter` yet), and the five `Workflow_*` tests fail. Every `scripts/*` case passes — `verify.ps1` already carries the filter, and the workflow already runs the four verbs in order.

- [ ] **Step 3: Rewrite `ci.yml`**

Replace `.github/workflows/ci.yml` entirely with:

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
      - run: dotnet restore SqlHarness.sln
      - run: dotnet build SqlHarness.sln --no-restore -warnaserror
      - run: dotnet test SqlHarness.sln --no-build --filter "FullyQualifiedName!~Integration" --logger trx --results-directory TestResults
      - name: Upload test results
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: test-results-${{ matrix.os }}
          path: TestResults
      - name: Format check
        if: matrix.os == 'ubuntu-latest'
        run: dotnet format SqlHarness.sln --no-restore --verify-no-changes
```

Note the two changes beyond the matrix: every `dotnet` command now names `SqlHarness.sln` explicitly, and the test stage gains the `--filter`. Both are required for the parity contract; naming the solution also stops a stray second solution file in the repository root from being built.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet build SqlHarness.sln -warnaserror && dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~CiGateParityTests"`

Expected: `Passed! - Failed: 0, Passed: 14` — three theory cases per gate across two theories (6), one more theory across three gates (3), and five `Workflow_*` facts.

- [ ] **Step 5: Lint the workflow**

Run `actionlint .github/workflows/ci.yml` if `actionlint` is on PATH.

Expected with actionlint: no output, exit 0.
Expected without it: read the file against the YAML in Step 3 and confirm `${{ }}` expressions are intact. A malformed workflow file silently disables all CI, so this check is not optional — record which of the two paths you took.

- [ ] **Step 6: Commit**

```powershell
git add .github/workflows/ci.yml tests/SqlHarness.Tests/CiGateParityTests.cs
git commit -m "ci(032): gate CI on Windows and Linux

Adds an os matrix, concurrency cancellation, a job timeout, an explicit
NuGet cache (setup-dotnet's own cache needs a lock file this repo will never
have) and trx artifacts. CiGateParityTests fails the build when any of the
three gates drifts from the others."
```

---

### Task 4: Document both gates and mark plan 032 partial

**Files:**
- Modify: `README.md` (the "Development" section, currently around line 281-289)
- Modify: `AGENTS.md` (the `## Verify changes` section added by plan 016)
- Modify: `plans/README.md` (row for plan 032)

**Interfaces:**
- Consumes: the exact command names `pwsh -NoProfile -File scripts/verify.ps1` and `pwsh -NoProfile -File scripts/verify-linux.ps1` from Tasks 1-2, and the `ci.yml` matrix from Task 3.
- Produces: no code. This task's deliverable is that no document states something false about the gates.

Plan 016's README sentence claims "CI's own test invocation is unfiltered". Task 3 made that false. Correcting it is required work, not an optional polish.

- [ ] **Step 1: Update `README.md`**

In the `## Development` section, replace the sentence that documents the gate so the two gates and their blind spots are all stated. The section must end up reading:

```markdown
## Development

Run both gates before committing:

```powershell
pwsh ./scripts/verify.ps1
pwsh ./scripts/verify-linux.ps1
```

`verify.ps1` runs the Windows gate and `verify-linux.ps1` runs the same four stages inside WSL on a case-sensitive Linux filesystem (provision it once with `pwsh ./scripts/setup-linux-gate.ps1`). Neither replaces CI, and neither sees what the other sees: the Windows gate cannot observe case-sensitivity or POSIX path and permission behaviour, and the Linux gate cannot observe Windows-only process, COM and approval-tray behaviour. A change is not done until both gates are green.

GitHub Actions runs both operating systems as a matrix and uploads the test results as an artifact, so a test that skips on one platform is visible rather than silently reported as a pass.

The .NET SDK version comes from `global.json` (currently `9.0.316`, `rollForward: latestPatch`).

```powershell
dotnet test
dotnet run --project src\SqlHarness.Cli -- --help
```
```

Keep the existing `dotnet test` / `dotnet run` examples.

- [ ] **Step 2: Update `AGENTS.md`**

In the `## Verify changes` section, extend the code block and the sentence so both gates are named. The section must end up reading:

```markdown
## Verify changes

```powershell
pwsh ./scripts/verify.ps1
pwsh ./scripts/verify-linux.ps1
```

> CI runs the same four stages on ubuntu-latest and windows-latest; tests that pass only on Windows are not done. `verify-linux.ps1` runs those stages inside WSL on a case-sensitive filesystem, which is the only way to see Linux-only behaviour before pushing. Provision it once with `pwsh ./scripts/setup-linux-gate.ps1`.
```

Leave the rest of `AGENTS.md` untouched.

- [ ] **Step 3: Update `plans/README.md`**

Change row 032's status cell from `TODO` to:

```
PARTIAL (matrix + hygiene landed on ci/plan-032-matrix; Steps 1-3, Pester and .gitattributes remain)
```

Leave row 016 exactly as plan 016 left it.

- [ ] **Step 4: Verify the claims are true**

Run: `dotnet build SqlHarness.sln -warnaserror && dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~CiGateParityTests|FullyQualifiedName~VerifyLinuxScriptTests|FullyQualifiedName~SetupLocalLinuxGateScriptTests"`

Expected: all pass. Then re-read the three edited documents and confirm, quoting each: that no sentence claims CI runs a single platform, that no sentence claims the Linux gate replaces CI, and that the `setup-linux-gate.ps1` command is documented as a one-time step.

- [ ] **Step 5: Confirm only in-scope files changed**

Run: `git diff --name-only main`

`git diff` compares `main` against the working tree, so this lists the whole branch, not just the
uncommitted part. Expected: exactly these ten paths — six deliverables plus four test files — and
nothing else.

```
.github/workflows/ci.yml
AGENTS.md
README.md
plans/README.md
scripts/setup-linux-gate.ps1
scripts/verify-linux.ps1
tests/SqlHarness.Tests/CiGateParityTests.cs
tests/SqlHarness.Tests/RepositoryFile.cs
tests/SqlHarness.Tests/SetupLocalLinuxGateScriptTests.cs
tests/SqlHarness.Tests/VerifyLinuxScriptTests.cs
```

If any other path appears, something outside the plan's scope was modified — investigate before
continuing.

- [ ] **Step 6: Run both gates on the final commit**

Run: `pwsh -NoProfile -File scripts/verify.ps1`
Expected: `verify: OK`, exit 0.

Run: `pwsh -NoProfile -File scripts/verify-linux.ps1`
Expected: `verify-linux: OK`, exit 0.

Both on the same commit. This is the plan's central claim: one codebase, green on both operating systems, verified locally before any push.

- [ ] **Step 7: Commit**

```powershell
git add README.md AGENTS.md plans/README.md
git commit -m "docs(032): document the Linux gate and both-platform CI

States what each gate can and cannot see, corrects the now-false claim that
CI's test invocation is unfiltered, and marks plan 032 partial."
```

---

## Done criteria

- [ ] `pwsh -NoProfile -File scripts/setup-linux-gate.ps1` succeeds and a second run is a no-op.
- [ ] `pwsh -NoProfile -File scripts/verify-linux.ps1` prints `verify-linux: OK` and exits 0.
- [ ] `pwsh -NoProfile -File scripts/verify.ps1` prints `verify: OK` and exits 0, on the same commit.
- [ ] `dotnet test SqlHarness.sln --no-build --filter "FullyQualifiedName!~Integration"` → 0 failed.
- [ ] `dotnet format SqlHarness.sln --no-restore --verify-no-changes` → exit 0.
- [ ] `ci.yml` contains `windows-latest`, `ubuntu-latest`, `fail-fast: false`, `concurrency`, `cancel-in-progress`, `timeout-minutes`, `actions/cache`, `--logger trx`, `actions/upload-artifact`.
- [ ] `git grep -n "9.0.316" -- scripts/setup-linux-gate.ps1 scripts/verify-linux.ps1` → no match.
- [ ] `plans/README.md` row 032 says PARTIAL and enumerates the remainder.
- [ ] Nothing pushed; no pull request opened.

## Maintenance notes

- A new gate is a new place to drift. Adding a stage means changing all three of `scripts/verify.ps1`, `scripts/verify-linux.ps1`, and `.github/workflows/ci.yml`, and `CiGateParityTests` will fail until you do.
- Never run the gate from `/mnt/d`. It is case-insensitive and cannot reproduce the Linux filesystem.
- The gate clone is disposable. If it misbehaves, delete `~/src/sqlharness-gate` and re-run `scripts/setup-linux-gate.ps1`.
- A test that skips on one platform should be visible as a skip in the uploaded trx artifact. Silent early `return` guards are what plan 032 Steps 1-3 remain for.