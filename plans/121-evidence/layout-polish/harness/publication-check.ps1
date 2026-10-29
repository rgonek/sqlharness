param([Parameter(Mandatory)][string]$Workspace, [switch]$VerifyExistingRestart)
$ErrorActionPreference = 'Stop'
function Protect-Output([string]$Value) {
    $safe = [regex]::Replace($Value, '([?&]t=)[^\s&]+', '$1<redacted>')
    return [regex]::Replace($safe, '(?i)("token"\s*:\s*")[^"]+(")', '$1<redacted>$2')
}
try {
    $installed = (Get-Command sqlharness -CommandType Application | Select-Object -First 1).Source
    $artifact = Join-Path $Workspace 'artifacts/publish-single/sqlharness.exe'
    $installedHash = (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash
    $artifactHash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash
    if ($installedHash -ne $artifactHash) { throw 'Installed binary differs from the published artifact.' }
    $version = (& $installed --version 2>&1 | ForEach-Object { $_.ToString() }) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw 'Installed binary did not report its version.' }
    $taskHome = [Environment]::GetEnvironmentVariable('SQLHARNESS_HOME')
    if (-not $taskHome) { $taskHome = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.sqlharness' }
    $endpointPath = Join-Path $taskHome 'dashboard.json'
    $previousPid = $null
    if (Test-Path -LiteralPath $endpointPath) {
        $previous = Get-Content -LiteralPath $endpointPath -Raw | ConvertFrom-Json
        $previousPid = $previous.pid
    }
    if ($VerifyExistingRestart) {
        $previousPid = $null
        $endpoint = Get-Content -LiteralPath $endpointPath -Raw | ConvertFrom-Json
        $bootstrap = [Uri]("http://127.0.0.1:" + $endpoint.port + "/?t=" + $endpoint.token)
        $restartExit = 0
    } else {
        $restartOutput = (& $installed dashboard --restart 2>&1 | ForEach-Object { $_.ToString() }) -join "`n"
        $restartExit = $LASTEXITCODE
        if ($restartExit -ne 0) { throw ('Dashboard restart failed: ' + (Protect-Output $restartOutput)) }
        $match = [regex]::Match($restartOutput, 'http://127\.0\.0\.1:\d+/\?t=[^\s]+')
        if (-not $match.Success) { throw 'Restart did not announce the expected loopback URL.' }
        $bootstrap = [Uri]$match.Value
        $endpoint = Get-Content -LiteralPath $endpointPath -Raw | ConvertFrom-Json
    }
    $dashboardProcess = Get-Process -Id $endpoint.pid
    if ($dashboardProcess.Path -ne $installed) { throw 'Restarted dashboard runs from a different executable.' }
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.CookieContainer = [System.Net.CookieContainer]::new()
    $handler.AllowAutoRedirect = $true
    $http = [System.Net.Http.HttpClient]::new($handler)
    try {
        $response = $http.GetAsync($bootstrap).GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) { throw ('Dashboard HTTP status: ' + [int]$response.StatusCode) }
        # The authenticated token URL is an HTML hand-off page, not an HTTP redirect.
        # CookieContainer keeps the cookie it sets; request the clean origin next.
        $origin = [Uri]('http://127.0.0.1:' + $endpoint.port + '/')
        $response = $http.GetAsync($origin).GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) { throw ('Authenticated dashboard HTTP status: ' + [int]$response.StatusCode) }
        $html = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $assetPaths = @([regex]::Matches($html, '(?:src|href)="(/assets/[^\"]+\.(?:js|css))"') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
        if ($assetPaths.Count -lt 2) { throw 'Dashboard did not return its JS and CSS entry assets.' }
        $origin = [Uri]('http://127.0.0.1:' + $endpoint.port + '/')
        $assets = foreach ($asset in $assetPaths) {
            $bytes = $http.GetByteArrayAsync([Uri]::new($origin, $asset)).GetAwaiter().GetResult()
            $servedHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes))
            $relative = $asset.TrimStart('/').Replace('/', [IO.Path]::DirectorySeparatorChar)
            $localPath = Join-Path (Join-Path $Workspace 'src/SqlHarness.Dashboard/ui/dist') $relative
            $builtHash = (Get-FileHash -LiteralPath $localPath -Algorithm SHA256).Hash
            if ($servedHash -ne $builtHash) { throw ('Dashboard serves a different built asset: ' + $asset) }
            [pscustomobject]@{ asset = $asset; sha256 = $servedHash; matchesBuiltAsset = $true }
        }
        $result = [ordered]@{
            installed = $installed
            artifact = $artifact
            installedSha256 = $installedHash
            artifactSha256 = $artifactHash
            version = (Protect-Output $version)
            restartCommand = 'sqlharness dashboard --restart'
            restartExitCode = $restartExit
            verificationReusedSuccessfulRestart = [bool]$VerifyExistingRestart
            previousPid = $previousPid
            dashboardPid = $endpoint.pid
            executablePath = $dashboardProcess.Path
            url = $origin.AbsoluteUri
            httpStatus = [int]$response.StatusCode
            servedAssets = @($assets)
        }
        $receipt = Join-Path $Workspace 'plans/121-evidence/layout-polish/publication.json'
        $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $receipt
        $result | ConvertTo-Json -Depth 6
    } finally {
        $http.Dispose()
        $handler.Dispose()
    }
} catch {
    Write-Error (Protect-Output $_.Exception.Message)
    exit 1
}
