#Requires -Version 7.0
<#
.SYNOPSIS
    Optional local PostgreSQL 16 + Pagila playground bootstrap.

.DESCRIPTION
    Creates or reuses fixed Docker resources (sqlharness-pg / sqlharness-pg-data
    on host port 5433) and restores Pagila when that database is absent.

    Fail-closed: never deletes or recreates conflicting Docker resources.
    Does not print the playground password or connection strings containing it.
    Does not edit ~/.sqlharness/targets.json.

.NOTES
    Requires non-empty process env SQLHARNESS_PG_PLAYGROUND_PASSWORD and Docker.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$containerName = 'sqlharness-pg'
$volumeName = 'sqlharness-pg-data'
$hostPort = 5433
$databaseName = 'pagila'
$image = 'postgres:16'
$schemaUrl = 'https://raw.githubusercontent.com/devrimgunduz/pagila/master/pagila-schema.sql'
$dataUrl = 'https://raw.githubusercontent.com/devrimgunduz/pagila/master/pagila-data.sql'
$containerSqlDir = '/tmp/sqlharness-pagila'
$containerSchemaPath = "$containerSqlDir/pagila-schema.sql"
$containerDataPath = "$containerSqlDir/pagila-data.sql"
$readyTimeout = [TimeSpan]::FromMinutes(3)
$readyPollSeconds = 2
if ($env:SQLHARNESS_PG_PLAYGROUND_READY_POLL_SECONDS -match '^\d+$') {
    $readyPollSeconds = [int]$env:SQLHARNESS_PG_PLAYGROUND_READY_POLL_SECONDS
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
        if ($arg -like 'PGPASSWORD=*') {
            $redacted += 'PGPASSWORD=***'
            continue
        }
        if ($arg -like 'POSTGRES_PASSWORD=*') {
            $redacted += 'POSTGRES_PASSWORD=***'
            continue
        }
        if (($arg -eq '--env' -or $arg -eq '-e') -and ($i + 1) -lt $DockerArgs.Count) {
            $next = [string]$DockerArgs[$i + 1]
            if ($next -like 'PGPASSWORD=*') {
                $redacted += $arg
                $redacted += 'PGPASSWORD=***'
                $i++
                continue
            }
            if ($next -like 'POSTGRES_PASSWORD=*') {
                $redacted += $arg
                $redacted += 'POSTGRES_PASSWORD=***'
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

function Invoke-PsqlInContainer {
    param(
        [Parameter(Mandatory)]
        [string]$Database,
        [string]$Command,
        [string]$File,
        [switch]$AllowFailure
    )

    # Password is passed only via container process env for psql; never Write-Host it.
    $dockerArgs = @(
        'exec'
        '--env'
        "PGPASSWORD=$($env:SQLHARNESS_PG_PLAYGROUND_PASSWORD)"
        $containerName
        'psql'
        '-U', 'postgres'
        '-d', $Database
        '-v', 'ON_ERROR_STOP=1'
        '-q'
    )
    if (-not [string]::IsNullOrWhiteSpace($File)) {
        $dockerArgs += @('-f', $File)
    }
    else {
        $dockerArgs += @('-tAc', $Command)
    }
    return Invoke-Docker -DockerArgs $dockerArgs -AllowFailure:$AllowFailure
}

function Test-HostPortFree {
    param([int]$Port)
    # Unit tests set this so a live playground on 5433 does not fail fake-docker runs.
    if ($env:SQLHARNESS_PG_PLAYGROUND_SKIP_HOST_PORT_CHECK -eq '1') {
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

    $hostPortFor5432 = $null
    if ($null -ne $portBindings -and $portBindings.PSObject.Properties.Name -contains '5432/tcp') {
        $bindings = @($portBindings.'5432/tcp')
        if ($bindings.Count -ge 1 -and $null -ne $bindings[0].HostPort) {
            $hostPortFor5432 = [string]$bindings[0].HostPort
        }
    }

    if ($hostPortFor5432 -ne [string]$hostPort) {
        throw "STOP: port mapping mismatch for container '$containerName' (expected host port $hostPort -> 5432/tcp, found '$hostPortFor5432')."
    }

    $mounts = @()
    try {
        $mounts = @($mountResult.Text | ConvertFrom-Json)
    }
    catch {
        throw "STOP: volume mounts for container '$containerName' could not be parsed."
    }

    $pgMount = $mounts | Where-Object { [string]$_.Destination -eq '/var/lib/postgresql/data' } | Select-Object -First 1
    if ($null -eq $pgMount) {
        throw "STOP: volume mount mismatch for container '$containerName' (missing mount at /var/lib/postgresql/data)."
    }

    $mountedVolume = $null
    if ($pgMount.PSObject.Properties.Name -contains 'Name' -and $pgMount.Name) {
        $mountedVolume = [string]$pgMount.Name
    }
    elseif ($pgMount.PSObject.Properties.Name -contains 'Source') {
        $mountedVolume = [string]$pgMount.Source
    }

    if ($mountedVolume -ne $volumeName) {
        throw "STOP: volume mount mismatch for container '$containerName' (expected named volume '$volumeName' at /var/lib/postgresql/data, found '$mountedVolume')."
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
    Write-SetupInfo "Creating container '$containerName' (host port $hostPort -> 5432)."

    $previousPgPassword = $env:POSTGRES_PASSWORD
    try {
        $env:POSTGRES_PASSWORD = $env:SQLHARNESS_PG_PLAYGROUND_PASSWORD
        $run = Invoke-Docker -DockerArgs @(
            'run', '--detach'
            '--name', $containerName
            '--publish', "${hostPort}:5432"
            '--volume', "${volumeName}:/var/lib/postgresql/data"
            '--env', 'POSTGRES_PASSWORD'
            $image
        ) -AllowFailure
        if ($run.ExitCode -ne 0) {
            throw 'Failed to create sqlharness-pg.'
        }
    }
    finally {
        if ($null -eq $previousPgPassword) {
            Remove-Item Env:POSTGRES_PASSWORD -ErrorAction SilentlyContinue
        }
        else {
            $env:POSTGRES_PASSWORD = $previousPgPassword
        }
    }
}

function Wait-PostgresReady {
    Write-SetupInfo 'Waiting for PostgreSQL to accept connections...'
    $deadline = [DateTime]::UtcNow + $readyTimeout
    while ([DateTime]::UtcNow -lt $deadline) {
        $result = Invoke-Docker -DockerArgs @(
            'exec', $containerName
            'pg_isready', '-U', 'postgres'
        ) -AllowFailure
        if ($result.ExitCode -eq 0) {
            Write-SetupInfo 'PostgreSQL is ready.'
            return
        }
        if ($readyPollSeconds -gt 0) {
            Start-Sleep -Seconds $readyPollSeconds
        }
    }
    throw "STOP: PostgreSQL in container '$containerName' did not become ready within $([int]$readyTimeout.TotalMinutes) minutes."
}

function Test-DatabaseExists {
    $result = Invoke-PsqlInContainer -Database 'postgres' -Command "SELECT 1 FROM pg_database WHERE datname = '$databaseName'"
    $line = ($result.Text -split '[\r\n]+' | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -First 1)
    return ($line -eq '1')
}

function Remove-IncompletePagilaDatabase {
    # Fail-closed cleanup so a partial CREATE+dump failure does not sticky-skip on the next run.
    Write-SetupInfo "Dropping incomplete database '$databaseName' so the next run can retry restore."
    Invoke-PsqlInContainer -Database 'postgres' -Command "DROP DATABASE IF EXISTS $databaseName WITH (FORCE)" -AllowFailure | Out-Null
}

function Restore-Pagila {
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetRandomFileName())
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    $localSchema = Join-Path $tempDir 'pagila-schema.sql'
    $localData = Join-Path $tempDir 'pagila-data.sql'
    $databaseCreated = $false
    $restoreCompleted = $false

    try {
        Write-SetupInfo 'Downloading Pagila schema and data dumps...'
        Invoke-WebRequest -Uri $schemaUrl -OutFile $localSchema -UseBasicParsing
        Invoke-WebRequest -Uri $dataUrl -OutFile $localData -UseBasicParsing

        Write-SetupInfo "Creating database '$databaseName'..."
        Invoke-PsqlInContainer -Database 'postgres' -Command "CREATE DATABASE $databaseName" | Out-Null
        $databaseCreated = $true

        Write-SetupInfo 'Copying dumps into container...'
        Invoke-Docker -DockerArgs @(
            'exec', $containerName
            'mkdir', '-p', $containerSqlDir
        ) | Out-Null

        Invoke-Docker -DockerArgs @(
            'cp', $localSchema, "${containerName}:$containerSchemaPath"
        ) | Out-Null
        Invoke-Docker -DockerArgs @(
            'cp', $localData, "${containerName}:$containerDataPath"
        ) | Out-Null

        Write-SetupInfo "Restoring Pagila schema into '$databaseName'..."
        Invoke-PsqlInContainer -Database $databaseName -File $containerSchemaPath | Out-Null

        Write-SetupInfo "Restoring Pagila data into '$databaseName'..."
        Invoke-PsqlInContainer -Database $databaseName -File $containerDataPath | Out-Null

        Write-SetupInfo 'Removing dump copies from container...'
        Invoke-Docker -DockerArgs @(
            'exec', $containerName
            'rm', '-rf', $containerSqlDir
        ) -AllowFailure | Out-Null

        $restoreCompleted = $true
    }
    finally {
        if ($databaseCreated -and -not $restoreCompleted) {
            Remove-IncompletePagilaDatabase
        }
        if (Test-Path $tempDir) {
            Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue
        }
    }
}

function Write-ManualProfileHint {
    Write-SetupInfo ''
    Write-SetupInfo 'Merge this profile into ~/.sqlharness/targets.json manually (the script never writes that file):'
    Write-SetupInfo @'
{
  "local-pg": {
    "engine": "postgres",
    "server": "localhost,5433",
    "database": "pagila",
    "vars": {},
    "auth": "sql",
    "sqlUser": "postgres",
    "passwordEnvVar": "SQLHARNESS_PG_PLAYGROUND_PASSWORD",
    "trustServerCertificate": true
  }
}
'@
}

# --- main ---

if ([string]::IsNullOrWhiteSpace($env:SQLHARNESS_PG_PLAYGROUND_PASSWORD)) {
    throw 'Set SQLHARNESS_PG_PLAYGROUND_PASSWORD in the current process before running setup.'
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

Wait-PostgresReady

if (Test-DatabaseExists) {
    Write-SetupInfo "Database '$databaseName' already exists; skipping restore."
    Write-SetupInfo "Local Postgres playground is ready (container '$containerName', database '$databaseName')."
    Write-ManualProfileHint
    return
}

Write-SetupInfo "Database '$databaseName' not found; performing restore."
Restore-Pagila

if (-not (Test-DatabaseExists)) {
    throw "STOP: restore completed but database '$databaseName' was not found."
}

Write-SetupInfo "Local Postgres playground is ready (container '$containerName', database '$databaseName')."
Write-ManualProfileHint
