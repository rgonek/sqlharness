#Requires -Version 7.0
<#
.SYNOPSIS
    Local CI gate: UI install and check, restore, build, test (without integration tests), and format check.

.DESCRIPTION
    Installs and checks the dashboard UI, then runs the same four .NET stages as
    the GitHub Actions CI workflow on SqlHarness.sln, stopping at the first
    failure. Excludes integration tests and does not touch ~/.sqlharness.

.NOTES
    Requires the .NET SDK version pinned by global.json (currently 9.0.316,
    rollForward: latestPatch) and the Node version pinned by .nvmrc (npm on PATH).
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

function Invoke-Stage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [scriptblock]$Action
    )

    Write-Host "==> $Name"
    & $Action
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        exit $code
    }
}

Invoke-Stage -Name 'ui-install' -Action { npm ci --prefix src/SqlHarness.Dashboard/ui }
Invoke-Stage -Name 'ui-check' -Action { npm run check --prefix src/SqlHarness.Dashboard/ui }
Invoke-Stage -Name 'restore' -Action { dotnet restore SqlHarness.sln }
Invoke-Stage -Name 'build' -Action { dotnet build SqlHarness.sln --no-restore -warnaserror }
Invoke-Stage -Name 'test' -Action { dotnet test SqlHarness.sln --no-build --filter 'FullyQualifiedName!~Integration' }
Invoke-Stage -Name 'format' -Action { dotnet format SqlHarness.sln --no-restore --verify-no-changes }

Write-Host 'verify: OK'
