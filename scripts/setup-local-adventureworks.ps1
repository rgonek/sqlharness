#Requires -Version 7.0
<#
.SYNOPSIS
    Optional local SQL Server 2022 + AdventureWorks2022 playground bootstrap.

.DESCRIPTION
    Creates or reuses fixed Docker resources (sqlharness-sql / sqlharness-sql-data
    on host port 14335) and restores AdventureWorks2022 when absent.

    Fail-closed: never deletes or recreates conflicting Docker resources.
    Does not print the playground password or connection strings.
    Does not edit ~/.sqlharness/targets.json.

.NOTES
    Requires non-empty process env SQLHARNESS_PLAYGROUND_PASSWORD and Docker.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$containerName = 'sqlharness-sql'
$volumeName = 'sqlharness-sql-data'
$hostPort = 14335
$databaseName = 'AdventureWorks2022'
$image = 'mcr.microsoft.com/mssql/server:2022-latest'
$backupUrl = 'https://github.com/Microsoft/sql-server-samples/releases/download/adventureworks/AdventureWorks2022.bak'
$containerBackupDir = '/var/opt/mssql/backup'
$containerBackupPath = "$containerBackupDir/AdventureWorks2022.bak"
$containerDataPath = '/var/opt/mssql/data/AdventureWorks2022.mdf'
$containerLogPath = '/var/opt/mssql/data/AdventureWorks2022_log.ldf'
$readyTimeout = [TimeSpan]::FromMinutes(3)
$readyPollSeconds = 2
if ($env:SQLHARNESS_PLAYGROUND_READY_POLL_SECONDS -match '^\d+$') {
    $readyPollSeconds = [int]$env:SQLHARNESS_PLAYGROUND_READY_POLL_SECONDS
}

function Write-SetupInfo {
    param([string]$Message)
    Write-Host $Message
}

function Get-RedactedDockerArgs {
    param([string[]]$DockerArgs)
    $redacted = @()
    for ($i = 0; $i -lt $DockerArgs.Count; $i++) {
        $arg = [string]$DockerArgs[$i]
        if ($arg -like 'SQLCMDPASSWORD=*') {
            $redacted += 'SQLCMDPASSWORD=***'
            continue
        }
        if (($arg -eq '--env' -or $arg -eq '-e') -and ($i + 1) -lt $DockerArgs.Count) {
            $next = [string]$DockerArgs[$i + 1]
            if ($next -like 'SQLCMDPASSWORD=*' -or $next -like 'MSSQL_SA_PASSWORD=*') {
                $redacted += $arg
                $prefix = ($next -split '=', 2)[0]
                $redacted += ($prefix + '=***')
                $i++
                continue
            }
        }
        $redacted += $arg
    }
    return $redacted
}

function Invoke-Docker {
    param(
        [Parameter(Mandatory)]
        [string[]]$DockerArgs,
        [switch]$AllowFailure
    )

    $output = & docker @DockerArgs 2>&1
    $code = $LASTEXITCODE
    if (-not $AllowFailure -and $code -ne 0) {
        $text = ($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
        $safeArgs = Get-RedactedDockerArgs -DockerArgs $DockerArgs
        throw "Docker command failed (exit $code): docker $($safeArgs -join ' ')$([Environment]::NewLine)$text"
    }
    return @{
        ExitCode = $code
        Output   = $output
        Text     = (($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine).Trim()
    }
}

function Invoke-SqlcmdInContainer {
    param(
        [Parameter(Mandatory)]
        [string]$Query,
        [switch]$AllowFailure
    )

    # SET NOCOUNT ON avoids trailing "(N rows affected)" lines that break simple parsers.
    # Keep the batch single-line: multi-line -Q payloads are fragile under Windows docker exec.
    $batch = $Query.Trim()
    if ($batch -notmatch '(?i)^\s*SET\s+NOCOUNT\s+ON') {
        $batch = "SET NOCOUNT ON; $batch"
    }
    $batch = ($batch -replace '[\r\n]+', ' ').Trim()

    # Password is passed only via container process env for sqlcmd; never Write-Host it.
    $dockerArgs = @(
        'exec'
        '--env'
        "SQLCMDPASSWORD=$($env:SQLHARNESS_PLAYGROUND_PASSWORD)"
        $containerName
        '/opt/mssql-tools18/bin/sqlcmd'
        '-S', 'localhost'
        '-U', 'sa'
        '-C'
        '-Q', $batch
        '-b'
        '-W'
        '-h', '-1'
    )
    return Invoke-Docker -DockerArgs $dockerArgs -AllowFailure:$AllowFailure
}

function Get-QuotedSqlIdentifier {
    param([Parameter(Mandatory)][string]$Name)
    return '[' + ($Name -replace ']', ']]') + ']'
}

function Get-QuotedSqlUnicodeLiteral {
    param([Parameter(Mandatory)][string]$Value)
    return "N'" + ($Value -replace "'", "''") + "'"
}

function Get-FirstSqlcmdDataLine {
    param([string]$Text)

    foreach ($line in @($Text -split '[\r\n]+')) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed)) { continue }
        # Ignore sqlcmd chatter that can appear when NOCOUNT is off or tools differ.
        if ($trimmed -match '^\(\d+\s+rows?\s+affected\)$') { continue }
        if ($trimmed -match '^(Msg\s+\d+|Sqlcmd:|Changed database context)') { continue }
        return $trimmed
    }
    return $null
}

function Test-HostPortFree {
    param([int]$Port)
    # Unit tests set this so a live playground on 14335 does not fail fake-docker runs.
    if ($env:SQLHARNESS_PLAYGROUND_SKIP_HOST_PORT_CHECK -eq '1') {
        return $true
    }
    $listeners = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
    return ($listeners.Count -eq 0)
}

function Get-ExistingContainerName {
    $result = Invoke-Docker -DockerArgs @(
        'ps', '-a'
        '--filter', "name=^/${containerName}$"
        '--format', '{{.Names}}'
    )
    $names = @($result.Text -split '[\r\n]+' | Where-Object { $_ -and $_.Trim() })
    if ($names -contains $containerName) {
        return $containerName
    }
    return $null
}

function Assert-CompatibleContainer {
    $portResult = Invoke-Docker -DockerArgs @(
        'inspect', $containerName
        '--format', '{{json .HostConfig.PortBindings}}'
    )
    $mountResult = Invoke-Docker -DockerArgs @(
        'inspect', $containerName
        '--format', '{{json .Mounts}}'
    )
    $runningResult = Invoke-Docker -DockerArgs @(
        'inspect', $containerName
        '--format', '{{.State.Running}}'
    )

    $portBindings = $null
    try {
        $portBindings = $portResult.Text | ConvertFrom-Json
    }
    catch {
        throw "STOP: port bindings for container '$containerName' could not be parsed."
    }

    $hostPortFor1433 = $null
    if ($null -ne $portBindings -and $portBindings.PSObject.Properties.Name -contains '1433/tcp') {
        $bindings = @($portBindings.'1433/tcp')
        if ($bindings.Count -ge 1 -and $null -ne $bindings[0].HostPort) {
            $hostPortFor1433 = [string]$bindings[0].HostPort
        }
    }

    if ($hostPortFor1433 -ne [string]$hostPort) {
        throw "STOP: port mapping mismatch for container '$containerName' (expected host port $hostPort -> 1433/tcp, found '$hostPortFor1433')."
    }

    $mounts = @()
    try {
        $mounts = @($mountResult.Text | ConvertFrom-Json)
    }
    catch {
        throw "STOP: volume mounts for container '$containerName' could not be parsed."
    }

    $mssqlMount = $mounts | Where-Object { [string]$_.Destination -eq '/var/opt/mssql' } | Select-Object -First 1
    if ($null -eq $mssqlMount) {
        throw "STOP: volume mount mismatch for container '$containerName' (missing mount at /var/opt/mssql)."
    }

    $mountedVolume = $null
    if ($mssqlMount.PSObject.Properties.Name -contains 'Name' -and $mssqlMount.Name) {
        $mountedVolume = [string]$mssqlMount.Name
    }
    elseif ($mssqlMount.PSObject.Properties.Name -contains 'Source') {
        $mountedVolume = [string]$mssqlMount.Source
    }

    if ($mountedVolume -ne $volumeName) {
        throw "STOP: volume mount mismatch for container '$containerName' (expected named volume '$volumeName' at /var/opt/mssql, found '$mountedVolume')."
    }

    $isRunning = ($runningResult.Text.Trim().ToLowerInvariant() -eq 'true')
    return $isRunning
}

function Ensure-Volume {
    $inspect = Invoke-Docker -DockerArgs @('volume', 'inspect', $volumeName) -AllowFailure
    if ($inspect.ExitCode -eq 0) {
        Write-SetupInfo "Reusing existing Docker volume '$volumeName'."
        return
    }
    Write-SetupInfo "Creating Docker volume '$volumeName'."
    Invoke-Docker -DockerArgs @('volume', 'create', $volumeName) | Out-Null
}

function New-PlaygroundContainer {
    Write-SetupInfo "Creating container '$containerName' (host port $hostPort -> 1433)."

    $previousSqlPassword = $env:MSSQL_SA_PASSWORD
    try {
        $env:MSSQL_SA_PASSWORD = $env:SQLHARNESS_PLAYGROUND_PASSWORD
        $run = Invoke-Docker -DockerArgs @(
            'run', '--detach'
            '--name', $containerName
            '--publish', "${hostPort}:1433"
            '--volume', "${volumeName}:/var/opt/mssql"
            '--env', 'ACCEPT_EULA=Y'
            '--env', 'MSSQL_PID=Developer'
            '--env', 'MSSQL_SA_PASSWORD'
            $image
        ) -AllowFailure
        if ($run.ExitCode -ne 0) {
            throw 'Failed to create sqlharness-sql.'
        }
    }
    finally {
        if ($null -eq $previousSqlPassword) {
            Remove-Item Env:MSSQL_SA_PASSWORD -ErrorAction SilentlyContinue
        }
        else {
            $env:MSSQL_SA_PASSWORD = $previousSqlPassword
        }
    }
}

function Wait-SqlServerReady {
    Write-SetupInfo 'Waiting for SQL Server to accept connections...'
    $deadline = [DateTime]::UtcNow + $readyTimeout
    $attempt = 0
    while ([DateTime]::UtcNow -lt $deadline) {
        $attempt++
        $result = Invoke-SqlcmdInContainer -Query 'SELECT 1' -AllowFailure
        if ($result.ExitCode -eq 0) {
            Write-SetupInfo 'SQL Server is ready.'
            return
        }
        if ($readyPollSeconds -gt 0) {
            Start-Sleep -Seconds $readyPollSeconds
        }
    }
    throw "STOP: SQL Server in container '$containerName' did not become ready within $([int]$readyTimeout.TotalMinutes) minutes."
}

function Test-DatabaseExists {
    # DB_ID returns NULL when the database is absent. Real sqlcmd often appends
    # blank lines and "(1 rows affected)" unless NOCOUNT is on — never treat that
    # trailing chatter as proof the database exists.
    $result = Invoke-SqlcmdInContainer -Query "SELECT CAST(DB_ID(N'$databaseName') AS nvarchar(32));"
    $line = Get-FirstSqlcmdDataLine -Text $result.Text
    if ([string]::IsNullOrWhiteSpace($line)) {
        return $false
    }
    if ($line -eq 'NULL') {
        return $false
    }
    # Present databases yield a positive integer database_id.
    return ($line -match '^\d+$')
}

function Get-BackupLogicalFiles {
    param([string]$BackupPathInContainer)

    $query = "RESTORE FILELISTONLY FROM DISK = N'$BackupPathInContainer'"
    $result = Invoke-SqlcmdInContainer -Query $query
    $lines = @(
        $result.Text -split '[\r\n]+' |
            ForEach-Object { $_.Trim() } |
            Where-Object {
                -not [string]::IsNullOrWhiteSpace($_) -and
                $_ -notmatch '^\(\d+\s+rows?\s+affected\)$' -and
                $_ -notmatch '^(LogicalName|---)'
            }
    )

    $dataName = $null
    $logName = $null
    $dataCount = 0
    $logCount = 0

    foreach ($line in $lines) {
        # Prefer tabular output with Type column; sqlcmd -W -h -1 yields space-separated columns.
        $parts = @($line -split '\s+' | Where-Object { $_ })
        if ($parts.Count -lt 3) { continue }

        $logical = $parts[0]
        $type = $null
        foreach ($p in $parts) {
            if ($p -eq 'D' -or $p -eq 'L') {
                $type = $p
                break
            }
        }
        if ($type -eq 'D') {
            $dataCount++
            if (-not $dataName) { $dataName = $logical }
        }
        elseif ($type -eq 'L') {
            $logCount++
            if (-not $logName) { $logName = $logical }
        }
    }

    if ($dataCount -ne 1 -or $logCount -ne 1 -or -not $dataName -or -not $logName) {
        throw 'STOP: expected one AdventureWorks2022 data file and one log file.'
    }

    return @{
        Data = $dataName
        Log  = $logName
    }
}

function Restore-AdventureWorks {
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetRandomFileName())
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    $localBak = Join-Path $tempDir 'AdventureWorks2022.bak'

    try {
        Write-SetupInfo 'Downloading official AdventureWorks2022 backup...'
        Invoke-WebRequest -Uri $backupUrl -OutFile $localBak -UseBasicParsing

        Write-SetupInfo 'Copying backup into container...'
        Invoke-Docker -DockerArgs @(
            'exec', $containerName
            'mkdir', '-p', $containerBackupDir
        ) | Out-Null

        Invoke-Docker -DockerArgs @(
            'cp', $localBak, "${containerName}:$containerBackupPath"
        ) | Out-Null

        Write-SetupInfo 'Reading backup logical file names...'
        $logical = Get-BackupLogicalFiles -BackupPathInContainer $containerBackupPath
        # N'...' MOVE targets are single-line safe for docker exec + sqlcmd -Q.
        $dataLit = Get-QuotedSqlUnicodeLiteral -Value $logical.Data
        $logLit = Get-QuotedSqlUnicodeLiteral -Value $logical.Log
        $dbId = Get-QuotedSqlIdentifier -Name $databaseName
        $bakLit = Get-QuotedSqlUnicodeLiteral -Value $containerBackupPath
        $dataPathLit = Get-QuotedSqlUnicodeLiteral -Value $containerDataPath
        $logPathLit = Get-QuotedSqlUnicodeLiteral -Value $containerLogPath

        Write-SetupInfo "Restoring database '$databaseName'..."
        $restoreSql = "RESTORE DATABASE $dbId FROM DISK = $bakLit WITH MOVE $dataLit TO $dataPathLit, MOVE $logLit TO $logPathLit, REPLACE"
        Invoke-SqlcmdInContainer -Query $restoreSql | Out-Null

        Write-SetupInfo 'Removing backup copy from container...'
        Invoke-Docker -DockerArgs @(
            'exec', $containerName
            'rm', '-f', $containerBackupPath
        ) -AllowFailure | Out-Null
    }
    finally {
        if (Test-Path $tempDir) {
            Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue
        }
    }
}

# --- main ---

if ([string]::IsNullOrWhiteSpace($env:SQLHARNESS_PLAYGROUND_PASSWORD)) {
    throw 'Set SQLHARNESS_PLAYGROUND_PASSWORD in the current process before running setup.'
}

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker CLI is unavailable.'
}

docker info *> $null
if ($LASTEXITCODE -ne 0) {
    throw 'Docker daemon is unavailable.'
}

$existing = Get-ExistingContainerName
if ($existing) {
    Write-SetupInfo "Found existing container '$containerName'; validating configuration..."
    $isRunning = Assert-CompatibleContainer
    if (-not $isRunning) {
        Write-SetupInfo "Starting stopped container '$containerName'..."
        Invoke-Docker -DockerArgs @('start', $containerName) | Out-Null
    }
    else {
        Write-SetupInfo "Reusing running container '$containerName'."
    }
}
else {
    if (-not (Test-HostPortFree -Port $hostPort)) {
        throw "STOP: host port $hostPort is already in use."
    }
    Ensure-Volume
    New-PlaygroundContainer
}

Wait-SqlServerReady

if (Test-DatabaseExists) {
    Write-SetupInfo "Database '$databaseName' already exists; skipping restore."
    Write-SetupInfo "Local AdventureWorks playground is ready (container '$containerName', database '$databaseName')."
    return
}

Write-SetupInfo "Database '$databaseName' not found; performing restore."
Restore-AdventureWorks

if (-not (Test-DatabaseExists)) {
    throw "STOP: restore completed but database '$databaseName' was not found."
}

Write-SetupInfo "Local AdventureWorks playground is ready (container '$containerName', database '$databaseName')."
