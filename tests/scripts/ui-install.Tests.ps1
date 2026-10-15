# Pester 3.4-compatible unit tests for ui-install.ps1
# Uses a fake npm on PATH and a temporary UI directory; never installs anything.

$ErrorActionPreference = 'Stop'

$script:RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$script:UiInstall = Join-Path $script:RepoRoot 'scripts\ui-install.ps1'

. $script:UiInstall

function New-FixtureUi {
    param([string]$LockContent = '{"lockfileVersion":3}')

    $root = Join-Path ([System.IO.Path]::GetTempPath()) ('sqlharness-ui-' + [Guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Path (Join-Path $root 'node_modules') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $root 'package-lock.json') -Value $LockContent -NoNewline
    return $root
}

function New-FakeNpmRoot {
    param([int]$ExitCode = 0, [switch]$NoBinShim)

    $root = Join-Path ([System.IO.Path]::GetTempPath()) ('sqlharness-fake-npm-' + [Guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    $script_ = "node_modules\.bin"
    $body = @"
@echo off
>"$root\calls.txt" echo %*
if /I not "$NoBinShim"=="$true" mkdir "node_modules\.bin" 2>nul
exit /b $ExitCode
"@
    Set-Content -LiteralPath (Join-Path $root 'npm.cmd') -Value $body -NoNewline
    return [pscustomobject]@{ Root = $root }
}

function Get-NpmCalls {
    param([string]$FakeRoot)

    $calls = Join-Path $FakeRoot 'calls.txt'
    if (-not (Test-Path $calls)) { return $null }
    return (Get-Content -LiteralPath $calls -Raw).Trim()
}

function Invoke-Install {
    param([string]$UiDirectory, [string]$FakeRoot, [switch]$Force)

    $previous = $env:PATH
    try {
        $env:PATH = $FakeRoot + [IO.Path]::PathSeparator + $previous
        $captured = & { Install-UiDependencies -UiDirectory $UiDirectory -Force:$Force } *>&1
        return [pscustomobject]@{
            Output   = (@($captured) -join "`n")
            Result   = $captured | Where-Object { $_ -is [psobject] -and $null -ne $_.Ran } | Select-Object -Last 1
            ExitCode = $global:LASTEXITCODE
        }
    }
    finally {
        $env:PATH = $previous
    }
}

Describe 'ui-install.ps1' {
    $ui = $null
    $fake = $null

    BeforeEach {
        $ui = New-FixtureUi
        $fake = New-FakeNpmRoot
    }

    AfterEach {
        foreach ($path in @($ui, $fake)) {
            if ($path -and (Test-Path $path)) { Remove-Item -Recurse -Force $path -ErrorAction SilentlyContinue }
        }
    }

    It 'installs when node_modules has no stamp and reports why' {
        $result = Invoke-Install -UiDirectory $ui -FakeRoot $fake.Root

        $result.Output | Should Match 'no install stamp'
        Get-NpmCalls -FakeRoot $fake.Root | Should Match 'ci'
    }

    It 'writes a stamp after a successful install' {
        Invoke-Install -UiDirectory $ui -FakeRoot $fake.Root | Out-Null

        $state = Get-UiInstallState -UiDirectory $ui

        $state.Installed | Should Be $true
        $state.Reason | Should Match 'lockfile unchanged'
    }

    It 'skips npm entirely when the lockfile has not changed' {
        Invoke-Install -UiDirectory $ui -FakeRoot $fake.Root | Out-Null
        Remove-Item -LiteralPath (Join-Path $fake.Root 'calls.txt') -Force -ErrorAction SilentlyContinue

        $result = Invoke-Install -UiDirectory $ui -FakeRoot $fake.Root

        $result.Output | Should Match 'up to date'
        Get-NpmCalls -FakeRoot $fake.Root | Should BeNullOrEmpty
    }

    It 'installs again when the lockfile changes' {
        Invoke-Install -UiDirectory $ui -FakeRoot $fake.Root | Out-Null
        Set-Content -LiteralPath (Join-Path $ui 'package-lock.json') -Value '{"lockfileVersion":3,"changed":true}' -NoNewline

        $result = Invoke-Install -UiDirectory $ui -FakeRoot $fake.Root

        $result.Output | Should Match 'package-lock.json changed'
        Get-NpmCalls -FakeRoot $fake.Root | Should Match 'ci'
    }

    It 'installs when node_modules disappears even though a stamp remains elsewhere' {
        Invoke-Install -UiDirectory $ui -FakeRoot $fake.Root | Out-Null
        Remove-Item -LiteralPath (Join-Path $ui 'node_modules') -Recurse -Force

        $result = Invoke-Install -UiDirectory $ui -FakeRoot $fake.Root

        $result.Output | Should Match 'node_modules is missing'
    }

    It 'does not stamp an install whose node_modules bin shims are missing' {
        # npm ci can report success while emitting TAR_ENTRY_ERROR and leaving a
        # tree without tsc, which the next stage then fails to find.
        Remove-Item -LiteralPath (Join-Path $ui 'node_modules') -Recurse -Force
        $broken = New-FakeNpmRoot -NoBinShim
        try {
            $result = Invoke-Install -UiDirectory $ui -FakeRoot $broken.Root

            $result.Output | Should Match 'not stamping'
            $result.Result.ExitCode | Should Be 1
            $state = Get-UiInstallState -UiDirectory $ui
            $state.Installed | Should Be $false
        }
        finally {
            Remove-Item -Recurse -Force $broken -ErrorAction SilentlyContinue
        }
    }

    It 'does not stamp an install that failed' {
        Remove-Item -LiteralPath (Join-Path $ui 'node_modules') -Recurse -Force
        $failing = New-FakeNpmRoot -ExitCode 1
        try {
            $result = Invoke-Install -UiDirectory $ui -FakeRoot $failing.Root

            $result.Result.ExitCode | Should Be 1
            $state = Get-UiInstallState -UiDirectory $ui
            $state.Installed | Should Be $false
        }
        finally {
            Remove-Item -Recurse -Force $failing -ErrorAction SilentlyContinue
        }
    }
}