#Requires -Version 7.0
<#
.SYNOPSIS
    Captures Plan 052 step-1 evidence against an explicitly confirmed disposable PostgreSQL database.

.DESCRIPTION
    Runs only the PostgresReadOnlyProof integration test. It requires the existing
    SQLHARNESS_PG_INTEGRATION_CONNECTION_STRING opt-in and the exact database name
    supplied through -ConfirmDisposableDatabase. The test writes sanitized results
    and 100-repetition timing to plans/052-readonly-proof.md. This script never
    provisions or starts PostgreSQL.

.EXAMPLE
    $env:SQLHARNESS_PG_INTEGRATION_CONNECTION_STRING = 'Host=localhost;Port=5433;Database=pagila;Username=postgres;Password=...'
    pwsh ./scripts/prove-postgres-readonly.ps1 -ConfirmDisposableDatabase pagila
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ConfirmDisposableDatabase,

    [string]$ReportPath = 'plans/052-readonly-proof.md'
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($env:SQLHARNESS_PG_INTEGRATION_CONNECTION_STRING)) {
    throw 'Set SQLHARNESS_PG_INTEGRATION_CONNECTION_STRING explicitly before running the proof.'
}

$resolvedReportPath = [System.IO.Path]::GetFullPath($ReportPath)
if (Test-Path -LiteralPath $resolvedReportPath) {
    $existingReport = Get-Content -LiteralPath $resolvedReportPath -Raw
    if ($existingReport -notmatch '<!-- pending-proof-template: safe to replace after an authorized run -->') {
        throw "Proof report already contains non-template content; review or move it before capturing new evidence: $resolvedReportPath"
    }
}

$previousConfirmation = $env:SQLHARNESS_PG_READONLY_PROOF_CONFIRM_DATABASE
$previousReportPath = $env:SQLHARNESS_PG_READONLY_PROOF_REPORT
try {
    $env:SQLHARNESS_PG_READONLY_PROOF_CONFIRM_DATABASE = $ConfirmDisposableDatabase
    $env:SQLHARNESS_PG_READONLY_PROOF_REPORT = $resolvedReportPath

    dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj `
        --filter 'FullyQualifiedName~PostgresReadOnlyProofTests.Server_enforces_read_only_transaction_proof' `
        --logger 'console;verbosity=normal'
    if ($LASTEXITCODE -ne 0) {
        throw "Proof execution stopped with dotnet test exit code $LASTEXITCODE. A sanitized report is written when execution reaches the proof test."
    }

    $capturedReport = Get-Content -LiteralPath $resolvedReportPath -Raw
    if ($capturedReport -match '<!-- pending-proof-template:' -or $capturedReport -notmatch 'Evidence status: EXECUTED') {
        throw "No executed proof report was produced at $resolvedReportPath; confirm that the focused integration test ran."
    }

    Write-Host "Sanitized proof evidence written to $resolvedReportPath. Review every outcome before marking Plan 052 step 1 proven."
}
finally {
    if ($null -eq $previousConfirmation) {
        Remove-Item Env:SQLHARNESS_PG_READONLY_PROOF_CONFIRM_DATABASE -ErrorAction SilentlyContinue
    }
    else {
        $env:SQLHARNESS_PG_READONLY_PROOF_CONFIRM_DATABASE = $previousConfirmation
    }

    if ($null -eq $previousReportPath) {
        Remove-Item Env:SQLHARNESS_PG_READONLY_PROOF_REPORT -ErrorAction SilentlyContinue
    }
    else {
        $env:SQLHARNESS_PG_READONLY_PROOF_REPORT = $previousReportPath
    }
}
