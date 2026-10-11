#Requires -Version 7.0
<#
.SYNOPSIS
    Regenerates src/SqlHarness.Core/Pricing/model-prices.json from models.dev before a release.
.DESCRIPTION
    Keeps only each model's cost object; review the diff like any other change. Builds never download prices.
#>
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$api = Invoke-RestMethod -Uri 'https://models.dev/api.json' -TimeoutSec 60
$providers = [ordered]@{}
foreach ($provider in ($api.PSObject.Properties | Sort-Object Name)) {
    $models = [ordered]@{}
    foreach ($model in ($provider.Value.models.PSObject.Properties | Sort-Object Name)) {
        if ($null -ne $model.Value.cost) { $models[$model.Name] = $model.Value.cost }
    }
    if ($models.Count -gt 0) { $providers[$provider.Name] = $models }
}
$snapshot = [ordered]@{ source = 'models.dev'; fetchedAt = [DateTimeOffset]::UtcNow.ToString('O'); providers = $providers }
$path = Join-Path $repo 'src/SqlHarness.Core/Pricing/model-prices.json'
$snapshot | ConvertTo-Json -Depth 12 -Compress | Set-Content -LiteralPath $path -Encoding utf8NoBOM
Write-Host "Wrote $path"
