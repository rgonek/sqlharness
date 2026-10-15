#Requires -Version 7.0
<#
.SYNOPSIS
    Installs the dashboard UI dependencies only when the lockfile changed.

.DESCRIPTION
    `npm ci` deletes and reinstalls node_modules, which took 64s on every gate
    run even when nothing about the lockfile had changed. This resolves the
    install state first: node_modules must exist and carry a stamp holding the
    SHA-256 of package-lock.json from the last successful install. An unchanged
    tree skips the install and the stage reports itself as skipped.

    The stamp lives inside node_modules, so removing node_modules always forces a
    reinstall and the stamp cannot outlive the tree it describes.

.NOTES
    Dot-source or call this file. It defines functions and runs nothing.
#>

$script:UiStampFileName = '.sqlharness-install-stamp'

function Get-UiInstallStamp {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$UiDirectory)

    $lockPath = Join-Path $UiDirectory 'package-lock.json'
    if (-not (Test-Path -LiteralPath $lockPath)) {
        throw "No package-lock.json in '$UiDirectory'."
    }

    # Lowercase hex so the Linux gate can compare the same stamp with sha256sum.
    return (Get-FileHash -LiteralPath $lockPath -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-UiInstallState {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$UiDirectory)

    $modulesPath = Join-Path $UiDirectory 'node_modules'
    if (-not (Test-Path -LiteralPath $modulesPath)) {
        return [pscustomobject]@{ Installed = $false; Reason = 'node_modules is missing' }
    }

    $stampPath = Join-Path $modulesPath $script:UiStampFileName
    if (-not (Test-Path -LiteralPath $stampPath)) {
        return [pscustomobject]@{ Installed = $false; Reason = 'no install stamp' }
    }

    $stamp = (Get-Content -LiteralPath $stampPath -Raw).Trim()
    $expected = Get-UiInstallStamp -UiDirectory $UiDirectory
    if ($stamp -ne $expected) {
        return [pscustomobject]@{ Installed = $false; Reason = 'package-lock.json changed' }
    }

    return [pscustomobject]@{ Installed = $true; Reason = 'lockfile unchanged' }
}

function Install-UiDependencies {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$UiDirectory,
        [switch]$Force
    )

    if (-not $Force) {
        $state = Get-UiInstallState -UiDirectory $UiDirectory
        if ($state.Installed) {
            Write-Host "ui: up to date ($($state.Reason))"
            return [pscustomobject]@{ Ran = $false; ExitCode = 0 }
        }
        Write-Host "ui: installing ($($state.Reason))"
    }

    Push-Location $UiDirectory
    try {
        & npm ci --no-fund --no-audit
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    if ($exitCode -eq 0) {
        # npm ci reports success while emitting TAR_ENTRY_ERROR for files that
        # vanished mid-extract, so only stamp an install whose bin shims exist.
        if (-not (Test-Path -LiteralPath (Join-Path $UiDirectory 'node_modules\.bin'))) {
            Write-Host 'ui: npm ci reported success but node_modules\.bin is missing; not stamping.'
            return [pscustomobject]@{ Ran = $true; ExitCode = 1 }
        }
        $stamp = Get-UiInstallStamp -UiDirectory $UiDirectory
        Set-Content -LiteralPath (Join-Path $UiDirectory "node_modules\$script:UiStampFileName") -Value $stamp
    }

    return [pscustomobject]@{ Ran = $true; ExitCode = $exitCode }
}