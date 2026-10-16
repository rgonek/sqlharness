#Requires -Version 7.0
<#
.SYNOPSIS
    Runs the CI gate inside WSL on a case-sensitive Linux filesystem.

.DESCRIPTION
    Syncs the current branch into the disposable gate clone created by
    scripts/setup-linux-gate.ps1, checks that the provisioned Node matches
    .nvmrc, installs the dashboard UI, then runs the same three .NET stages CI
    runs: build -warnaserror (which restores implicitly), test, and format
    --verify-no-changes. The UI check runs beside the build inside the build
    stage, so the stage lines here stay sequential.

    The gate clone is reset to the branch tip, so the format stage checks the
    C# files the branch changed relative to origin/main, not the working tree:
    a working-tree diff inside the clone is always empty. The file list is
    resolved here, on Windows, and passed into the stage.

    Output is written for an agent first: stage output goes to a log file, a
    green stage prints one line, a red stage prints the stage, the exit code, a
    short tail and the log path. Use -Stream to watch a run.

    Stops at the first failing stage and exits with that stage's exit code.

    Fail-closed: this working tree is read and never written. The gate clone is
    the only thing modified. Never writes ~/.sqlharness and never connects to a
    database.

.NOTES
    Run scripts/setup-linux-gate.ps1 first. Override the distribution with
    -Distro.
#>
[CmdletBinding()]
param(
    [string]$Distro = 'Ubuntu',

    # Stream every stage instead of the compact per-stage summary.
    [switch]$Stream,

    # Where stage logs are written. Defaults to a directory under the temp path.
    [string]$LogDirectory
)

$ErrorActionPreference = 'Stop'

function Assert-WslDistroVersion2 {
    param(
        [Parameter(Mandatory)][string]$Distro
    )

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'wsl.exe'
    $psi.Arguments = '-l -v'
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::Unicode
    $psi.StandardErrorEncoding = [System.Text.Encoding]::Unicode
    $psi.CreateNoWindow = $true

    $proc = [System.Diagnostics.Process]::Start($psi)
    $stdout = $proc.StandardOutput.ReadToEnd()
    $stderr = $proc.StandardError.ReadToEnd()
    $proc.WaitForExit()

    if ($proc.ExitCode -ne 0) {
        throw "Could not list WSL distributions. Ensure WSL is installed and the '$Distro' distribution is available. Details: $stderr"
    }

    $found = $false
    $lines = $stdout -split "`r?`n" | Where-Object { $_ -match '\S' }
    foreach ($line in $lines) {
        if ($line -match '^\s*NAME\s+STATE\s+VERSION\s*$') { continue }
        $pattern = '^\s*\*?\s*{0}\s+\S+\s+(\d+)\s*$' -f [regex]::Escape($Distro)
        if ($line -match $pattern) {
            if ($matches[1] -ne '2') {
                throw "WSL distribution '$Distro' is version $($matches[1]), but version 2 is required."
            }
            $found = $true
            break
        }
    }

    if (-not $found) {
        throw "WSL distribution '$Distro' was not found. Run 'wsl -l -v' to list distributions, then re-run with -Distro <name>."
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'gate-output.ps1')

if ([string]::IsNullOrWhiteSpace($LogDirectory)) {
    $LogDirectory = Join-Path ([System.IO.Path]::GetTempPath()) 'sqlharness-gate-logs\verify-linux'
}

$branch = (& git -C $repoRoot rev-parse --abbrev-ref HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($branch)) {
    throw 'Could not determine the current branch.'
}

Assert-WslDistroVersion2 -Distro $Distro

# Branch tips live in the common git directory. A linked worktree's .git file
# stores a Windows absolute gitdir (`D:/...`). WSL git treats that as a relative
# path and cannot fetch the worktree. Fetch the main checkout instead.
$commonDir = (& git -C $repoRoot rev-parse --git-common-dir).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commonDir)) {
    throw 'Could not determine the common git directory.'
}
if (-not [System.IO.Path]::IsPathRooted($commonDir)) {
    $commonDir = Join-Path $repoRoot $commonDir
}
$fetchRoot = Split-Path ([System.IO.Path]::GetFullPath($commonDir)) -Parent
if ([string]::IsNullOrWhiteSpace($fetchRoot)) {
    throw 'Could not determine the checkout to sync into the Linux gate.'
}

$windowsRepo = (& wsl -d $Distro -- wslpath -a ($fetchRoot -replace '\\', '/')).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($windowsRepo)) {
    throw "Could not translate the repository path '$fetchRoot' into a WSL path."
}

$wslHome = (& wsl -d $Distro -- bash -lc 'printf %s "$HOME"').Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($wslHome)) {
    throw "Could not determine the WSL home directory for distro '$Distro'."
}
$gateClone = "$wslHome/src/sqlharness-gate"

$script:stageCount = 0
$total = [System.Diagnostics.Stopwatch]::StartNew()

function Invoke-LinuxStage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string[]]$Lines,
        [string[]]$Arguments = @(),
        [switch]$Skipped,
        [string]$SkipReason
    )

    $script:stageCount++
    $result = Invoke-GateStage -Name $Name -LogDirectory $LogDirectory -Skipped:$Skipped -SkipReason $SkipReason -Action {
        # PATH is exported here rather than sourced from ~/.bashrc: Ubuntu's stock
        # .bashrc returns early in a non-interactive shell, so an rc-file PATH would
        # silently not apply and the gate would fail for the wrong reason. The line is
        # single-quoted so bash, not PowerShell, expands $HOME: a PowerShell expansion
        # puts a Windows path into the Linux PATH and the gate would silently test
        # whatever node and dotnet the distro happens to carry.
        # The provisioned Node (~/.node/current, from scripts/setup-linux-gate.ps1) comes
        # first so it wins over any Windows-interop `node`/`npm` on the WSL PATH.
        $script = @'
set -euo pipefail
export PATH="$HOME/.node/current/bin:$HOME/.dotnet:$PATH"
'@ + "`n" + ($Lines -join "`n")

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

            $run = Invoke-NativeStageCommand -FilePath 'wsl.exe' -Arguments (@('-d', $Distro, '--', 'bash', $stageWslPath) + $Arguments)
            $run.Output | ForEach-Object { Write-Host $_ }
            if ($run.ExitCode -ne 0) { throw "wsl exited with $($run.ExitCode)" }
        }
        finally {
            Remove-Item -LiteralPath $stageFile.FullName -ErrorAction SilentlyContinue
        }
    }

    Write-Host (Get-StageLine -Result $result)
    if ($Stream -and -not $result.Skipped -and (Test-Path -LiteralPath $result.LogPath)) {
        Get-Content -LiteralPath $result.LogPath | ForEach-Object { Write-Host $_ }
    }

    if ($result.ExitCode -eq 0) { return }

    $failure = Get-StageFailureText -Result $result -TailLines 40
    if ($failure) { Write-Host $failure }
    Write-Host (Get-GateSummaryLine -Gate 'verify-linux' -StageCount $script:stageCount -Seconds $total.Elapsed.TotalSeconds -FailureStage $Name -FailureExitCode $result.ExitCode)
    exit $result.ExitCode
}

Invoke-LinuxStage -Name 'sync' -Arguments @($gateClone, $windowsRepo, $branch) -Lines @(
    'cd "$1"',
    'git fetch "$2" "$3"',
    'git reset --hard FETCH_HEAD',
    'git clean -fd'
)

Invoke-LinuxStage -Name 'sdk' -Arguments @($gateClone) -Lines @(
    'cd "$1"',
    'dotnet --version'
)

Invoke-LinuxStage -Name 'node' -Arguments @($gateClone) -Lines @(
    'cd "$1"',
    'node_path="$(command -v node || true)"',
    'case "$node_path" in ""|/mnt/*) echo "No Linux node on PATH (found: ${node_path:-none}); run scripts/setup-linux-gate.ps1" >&2; exit 1 ;; esac',
    'node_version="$(node --version)"',
    'echo "$node_version"',
    'test "$node_version" = "v$(cat .nvmrc)" || { echo "node $node_version differs from .nvmrc; run scripts/setup-linux-gate.ps1" >&2; exit 1; }'
)

Invoke-LinuxStage -Name 'ui-install' -Arguments @($gateClone) -Lines @(
    'ui="$1/src/SqlHarness.Dashboard/ui"',
    # The same stamp contract as scripts/ui-install.ps1: npm ci only when
    # node_modules is missing or package-lock.json changed. It cost 54s a run.
    'lock_hash="$(sha256sum "$ui/package-lock.json" | cut -d" " -f1)"',
    'stamp="$ui/node_modules/.sqlharness-install-stamp"',
    'if [ -d "$ui/node_modules" ] && [ -f "$stamp" ] && [ "$(cat "$stamp")" = "$lock_hash" ]; then',
    '  echo "ui: up to date (lockfile unchanged)"',
    '  exit 0',
    'fi',
    'cd "$ui"',
    'npm ci --no-fund --no-audit',
    # npm ci can report success while leaving a tree without its bin shims.
    '[ -d "$ui/node_modules/.bin" ] || { echo "npm ci did not produce node_modules/.bin" >&2; exit 1; }',
    'printf %s "$lock_hash" > "$stamp"'
)

# The UI check needs no .NET artifacts, so it overlaps the build here rather than
# running in front of it. Each stage is one wsl.exe run of one bash script against the
# same gate clone, so a background `npm run check &` inside the build script is the
# only way to overlap the two: two concurrent WSL launches would race each other over
# the clone. Coupling them inside one invocation is why the stage lines here stay
# sequential. set +e around both commands is what lets the UI check be joined even when
# the build has already failed; the script then exits with the build's code, matching
# the Windows gate.
Invoke-LinuxStage -Name 'build' -Arguments @($gateClone) -Lines @(
    'cd "$1"',
    'npm run check --prefix src/SqlHarness.Dashboard/ui &',
    'ui_check_pid=$!',
    'set +e',
    'dotnet build SqlHarness.sln -warnaserror',
    'build_status=$?',
    'wait "$ui_check_pid"',
    'ui_check_status=$?',
    'set -e',
    'if [ "$ui_check_status" -ne 0 ]; then echo "==> ui-check FAILED (exit $ui_check_status)" >&2; fi',
    'if [ "$build_status" -ne 0 ]; then echo "==> build FAILED (exit $build_status)" >&2; fi',
    'if [ "$build_status" -ne 0 ]; then exit "$build_status"; fi',
    'exit "$ui_check_status"'
)

Invoke-LinuxStage -Name 'test' -Arguments @($gateClone) -Lines @(
    'cd "$1"',
    'dotnet test SqlHarness.sln --no-build --filter "FullyQualifiedName!~Integration" --logger "console;verbosity=normal"'
)

$changedFiles = @(& (Join-Path $PSScriptRoot 'format-changed.ps1') -Mode branch -ListOnly | Where-Object { $_ -match '\.cs$' })
Invoke-LinuxStage -Name 'format' -Skipped:($changedFiles.Count -eq 0) -SkipReason '0 changed C# files on the branch' -Arguments (@($gateClone) + $changedFiles) -Lines @(
    'cd "$1"',
    'shift',
    'changedFiles=("$@")',
    'dotnet format SqlHarness.sln --no-restore --include "${changedFiles[@]}" --verify-no-changes'
)

$total.Stop()
Write-Host (Get-GateSummaryLine -Gate 'verify-linux' -StageCount $script:stageCount -Seconds $total.Elapsed.TotalSeconds)
exit 0