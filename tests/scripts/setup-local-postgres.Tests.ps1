# Pester 3.4-compatible unit tests for setup-local-postgres.ps1
# Uses a fake docker on PATH; never talks to a real Docker daemon.

$ErrorActionPreference = 'Stop'

$script:RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$script:SetupScript = Join-Path $script:RepoRoot 'scripts\setup-local-postgres.ps1'
$script:FixturePassword = 'FixtureSecretPw_NotReal99'

function New-FakeDockerRoot {
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ("sqlharness-fake-docker-pg-" + [Guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $root 'logs') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $root 'state') -Force | Out-Null

    $cmdPath = Join-Path $root 'docker.cmd'
    $cmdBody = @'
@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "ROOT=%~dp0"
set "LOG=%ROOT%logs\calls.log"
set "ST=%ROOT%state"
set "LAST=%ROOT%logs\last-args.txt"
echo %*>"%LAST%"
>>"%LOG%" echo %*
if "%~1"=="" exit /b 1
if /I "%~1"=="info" goto cmd_info
if /I "%~1"=="ps" goto cmd_ps
if /I "%~1"=="inspect" goto cmd_inspect
if /I "%~1"=="volume" goto cmd_volume
if /I "%~1"=="run" goto cmd_run
if /I "%~1"=="start" goto cmd_start
if /I "%~1"=="cp" exit /b 0
if /I "%~1"=="exec" goto cmd_exec
echo fake docker: unhandled %*
exit /b 1
:cmd_info
if exist "%ST%\info.fail" (echo Cannot connect to the Docker daemon& exit /b 1)
echo Server Version: fake
exit /b 0
:cmd_ps
if exist "%ST%\container.exists" echo sqlharness-pg
exit /b 0
:cmd_inspect
if not exist "%ST%\container.exists" (echo Error: No such object: sqlharness-pg& exit /b 1)
findstr /C:"PortBindings" "%LAST%" >nul
if not errorlevel 1 goto inspect_ports
findstr /C:"Mounts" "%LAST%" >nul
if not errorlevel 1 goto inspect_mounts
findstr /C:"Running" "%LAST%" >nul
if not errorlevel 1 goto inspect_running
echo {}
exit /b 0
:inspect_ports
set /p HOSTPORT=<"%ST%\hostPort.txt"
echo {"5432/tcp":[{"HostIp":"","HostPort":"%HOSTPORT%"}]}
exit /b 0
:inspect_mounts
set /p VOLNAME=<"%ST%\volumeName.txt"
echo [{"Type":"volume","Name":"%VOLNAME%","Source":"/var/lib/docker/volumes/%VOLNAME%/_data","Destination":"/var/lib/postgresql/data","Driver":"local","Mode":"z","RW":true,"Propagation":""}]
exit /b 0
:inspect_running
if exist "%ST%\container.running" (echo true) else (echo false)
exit /b 0
:cmd_volume
if /I "%~2"=="inspect" goto vol_inspect
if /I "%~2"=="create" goto vol_create
exit /b 1
:vol_inspect
if exist "%ST%\volume.exists" (echo [{"Name":"sqlharness-pg-data"}]& exit /b 0)
echo Error: No such volume: sqlharness-pg-data
exit /b 1
:vol_create
type nul > "%ST%\volume.exists"
echo sqlharness-pg-data
exit /b 0
:cmd_run
type nul > "%ST%\container.exists"
type nul > "%ST%\container.running"
type nul > "%ST%\volume.exists"
echo fakecontainerid
exit /b 0
:cmd_start
type nul > "%ST%\container.running"
echo sqlharness-pg
exit /b 0
:cmd_exec
findstr /C:"pg_isready" "%LAST%" >nul
if not errorlevel 1 goto exec_ready
findstr /C:"DROP DATABASE" "%LAST%" >nul
if not errorlevel 1 goto exec_drop_db
findstr /C:"pg_database" "%LAST%" >nul
if not errorlevel 1 goto exec_db
findstr /C:"CREATE DATABASE" "%LAST%" >nul
if not errorlevel 1 goto exec_create_db
findstr /C:"pagila-schema.sql" "%LAST%" >nul
if not errorlevel 1 goto exec_dump
findstr /C:"pagila-data.sql" "%LAST%" >nul
if not errorlevel 1 goto exec_dump
exit /b 0
:exec_ready
if exist "%ST%\ready.fail" (echo accepting connections failed& exit /b 1)
echo /var/run/postgresql:5432 - accepting connections
exit /b 0
:exec_db
if exist "%ST%\db.exists" (
  echo 1
  exit /b 0
)
echo.
exit /b 0
:exec_create_db
type nul > "%ST%\db.exists"
exit /b 0
:exec_drop_db
if exist "%ST%\db.exists" del "%ST%\db.exists"
exit /b 0
:exec_dump
if exist "%ST%\restore.fail" (echo ERROR: fake dump restore failed& exit /b 1)
type nul > "%ST%\db.exists"
exit /b 0
'@
    Set-Content -Path $cmdPath -Value $cmdBody -Encoding ascii

    return $root
}

function Set-FakeDockerState {
    param(
        [Parameter(Mandatory)][string]$FakeRoot,
        [hashtable]$State
    )
    $st = Join-Path $FakeRoot 'state'
    Get-ChildItem -Path $st -Force -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

    $containerExists = [bool]$State.containerExists
    $running = [bool]$State.running
    $volumeExists = [bool]$State.volumeExists
    $dbExists = [bool]$State.dbExists
    $ready = if ($State.ContainsKey('ready')) { [bool]$State.ready } else { $true }
    $infoOk = if ($State.ContainsKey('infoOk')) { [bool]$State.infoOk } else { $true }
    $restoreOk = if ($State.ContainsKey('restoreOk')) { [bool]$State.restoreOk } else { $true }
    $hostPort = if ($State.hostPort) { [string]$State.hostPort } else { '5433' }
    $volumeName = if ($State.volumeName) { [string]$State.volumeName } else { 'sqlharness-pg-data' }

    Set-Content -Path (Join-Path $st 'hostPort.txt') -Value $hostPort -Encoding ascii -NoNewline
    Set-Content -Path (Join-Path $st 'volumeName.txt') -Value $volumeName -Encoding ascii -NoNewline

    if ($containerExists) { New-Item -ItemType File -Path (Join-Path $st 'container.exists') -Force | Out-Null }
    if ($running) { New-Item -ItemType File -Path (Join-Path $st 'container.running') -Force | Out-Null }
    if ($volumeExists) { New-Item -ItemType File -Path (Join-Path $st 'volume.exists') -Force | Out-Null }
    if ($dbExists) { New-Item -ItemType File -Path (Join-Path $st 'db.exists') -Force | Out-Null }
    if (-not $ready) { New-Item -ItemType File -Path (Join-Path $st 'ready.fail') -Force | Out-Null }
    if (-not $infoOk) { New-Item -ItemType File -Path (Join-Path $st 'info.fail') -Force | Out-Null }
    if (-not $restoreOk) { New-Item -ItemType File -Path (Join-Path $st 'restore.fail') -Force | Out-Null }
}

function Get-DockerCallLog {
    param([string]$FakeRoot)
    $log = Join-Path $FakeRoot 'logs\calls.log'
    if (Test-Path $log) {
        return @(Get-Content -Path $log -Encoding utf8 | Where-Object { $_ })
    }
    return @()
}

function Get-FakeStateSnapshot {
    param([string]$FakeRoot)
    $st = Join-Path $FakeRoot 'state'
    return @{
        containerExists = Test-Path (Join-Path $st 'container.exists')
        running         = Test-Path (Join-Path $st 'container.running')
        volumeExists    = Test-Path (Join-Path $st 'volume.exists')
        dbExists        = Test-Path (Join-Path $st 'db.exists')
    }
}

function Invoke-SetupScript {
    param(
        [string]$FakeRoot,
        [hashtable]$State,
        [string]$Password = $script:FixturePassword,
        [switch]$ClearPassword
    )

    if ($null -ne $State) {
        Set-FakeDockerState -FakeRoot $FakeRoot -State $State
    }

    $log = Join-Path $FakeRoot 'logs\calls.log'
    if (Test-Path $log) { Remove-Item $log -Force }

    $previousPath = $env:PATH
    $previousPassword = $env:SQLHARNESS_PG_PLAYGROUND_PASSWORD
    $previousPostgres = $env:POSTGRES_PASSWORD
    $previousPoll = $env:SQLHARNESS_PG_PLAYGROUND_READY_POLL_SECONDS
    $previousSkipPort = $env:SQLHARNESS_PG_PLAYGROUND_SKIP_HOST_PORT_CHECK
    $hadGlobalIwr = Test-Path Function:\global:Invoke-WebRequest

    try {
        $env:PATH = $FakeRoot + [IO.Path]::PathSeparator + $previousPath
        $env:SQLHARNESS_PG_PLAYGROUND_READY_POLL_SECONDS = '0'
        $env:SQLHARNESS_PG_PLAYGROUND_SKIP_HOST_PORT_CHECK = '1'
        if ($ClearPassword) {
            Remove-Item Env:SQLHARNESS_PG_PLAYGROUND_PASSWORD -ErrorAction SilentlyContinue
        }
        else {
            $env:SQLHARNESS_PG_PLAYGROUND_PASSWORD = $Password
        }

        function global:Invoke-WebRequest {
            [CmdletBinding()]
            param(
                [string]$Uri,
                [string]$OutFile,
                [switch]$UseBasicParsing
            )
            if ([string]::IsNullOrWhiteSpace($OutFile)) {
                throw 'unit-test Invoke-WebRequest mock requires -OutFile'
            }
            Set-Content -Path $OutFile -Value "-- fake pagila dump from $Uri" -Encoding utf8
        }

        $outputLines = New-Object System.Collections.Generic.List[string]
        $exitCode = 0
        $threw = $null

        $oldEap = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Stop'
            $global:LASTEXITCODE = 0
            $raw = & $script:SetupScript *>&1
            foreach ($line in @($raw)) {
                $outputLines.Add([string]$line) | Out-Null
            }
            $exitCode = 0
        }
        catch {
            $threw = $_
            $exitCode = 1
            $msg = $_.Exception.Message
            if ([string]::IsNullOrWhiteSpace($msg) -and $_.ToString()) { $msg = $_.ToString() }
            $outputLines.Add([string]$msg) | Out-Null
            $outputLines.Add([string]$_) | Out-Null
        }
        finally {
            $ErrorActionPreference = $oldEap
        }

        return @{
            ExitCode = $exitCode
            Output   = ($outputLines -join "`n")
            Lines    = $outputLines
            Error    = $threw
            Calls    = Get-DockerCallLog -FakeRoot $FakeRoot
            State    = Get-FakeStateSnapshot -FakeRoot $FakeRoot
        }
    }
    finally {
        $env:PATH = $previousPath
        if (-not $hadGlobalIwr) {
            Remove-Item Function:\global:Invoke-WebRequest -ErrorAction SilentlyContinue
        }
        if ($null -eq $previousPassword) {
            Remove-Item Env:SQLHARNESS_PG_PLAYGROUND_PASSWORD -ErrorAction SilentlyContinue
        }
        else {
            $env:SQLHARNESS_PG_PLAYGROUND_PASSWORD = $previousPassword
        }
        if ($null -eq $previousPostgres) {
            Remove-Item Env:POSTGRES_PASSWORD -ErrorAction SilentlyContinue
        }
        else {
            $env:POSTGRES_PASSWORD = $previousPostgres
        }
        if ($null -eq $previousPoll) {
            Remove-Item Env:SQLHARNESS_PG_PLAYGROUND_READY_POLL_SECONDS -ErrorAction SilentlyContinue
        }
        else {
            $env:SQLHARNESS_PG_PLAYGROUND_READY_POLL_SECONDS = $previousPoll
        }
        if ($null -eq $previousSkipPort) {
            Remove-Item Env:SQLHARNESS_PG_PLAYGROUND_SKIP_HOST_PORT_CHECK -ErrorAction SilentlyContinue
        }
        else {
            $env:SQLHARNESS_PG_PLAYGROUND_SKIP_HOST_PORT_CHECK = $previousSkipPort
        }
    }
}

Describe 'setup-local-postgres.ps1' {
    $fakeRoot = $null

    BeforeEach {
        $fakeRoot = New-FakeDockerRoot
    }

    AfterEach {
        if ($fakeRoot -and (Test-Path $fakeRoot)) {
            Remove-Item -Recurse -Force $fakeRoot -ErrorAction SilentlyContinue
        }
    }

    It 'script text contains fixed playground contract and never writes targets.json' {
        Test-Path $script:SetupScript | Should Be $true
        $text = Get-Content -Raw -Path $script:SetupScript
        $text | Should Match 'sqlharness-pg'
        $text | Should Match '5433'
        $text | Should Match 'SQLHARNESS_PG_PLAYGROUND_PASSWORD'
        $text | Should Match 'postgres:16'
        $text | Should Match 'pagila'
        if ($text -match '(?im)(Set-Content|Out-File).{0,120}targets\.json') {
            throw 'script must not write targets.json'
        }
    }

    It 'rejects a blank SQLHARNESS_PG_PLAYGROUND_PASSWORD' {
        Test-Path $script:SetupScript | Should Be $true

        $result = Invoke-SetupScript -FakeRoot $fakeRoot -ClearPassword
        $combined = $result.Output
        $combined | Should Match 'SQLHARNESS_PG_PLAYGROUND_PASSWORD'
        $result.ExitCode | Should Not Be 0
        @($result.Calls).Count | Should Be 0
    }

    It 'creates fixed resources when they are absent' {
        $state = @{
            containerExists = $false
            running         = $false
            hostPort        = '5433'
            volumeName      = 'sqlharness-pg-data'
            volumeExists    = $false
            dbExists        = $true
            ready           = $true
            infoOk          = $true
        }
        $result = Invoke-SetupScript -FakeRoot $fakeRoot -State $state
        if ($result.ExitCode -ne 0) {
            Write-Host "DEBUG output: $($result.Output)"
            Write-Host "DEBUG calls: $($result.Calls -join ' | ')"
        }
        $result.ExitCode | Should Be 0

        $callText = ($result.Calls -join "`n")
        $callText | Should Match 'volume create sqlharness-pg-data'
        $callText | Should Match '\brun\b'
        $callText | Should Match 'sqlharness-pg'
        $callText | Should Match '5433:5432'
        $callText | Should Match 'sqlharness-pg-data:/var/lib/postgresql/data'
        $callText | Should Match 'postgres:16'
        $result.Output | Should Not Match ([regex]::Escape($script:FixturePassword))
        $result.Output | Should Match 'local-pg'
        $result.Output | Should Match '"engine": "postgres"'
    }

    It 'reuses a compatible running container' {
        $state = @{
            containerExists = $true
            running         = $true
            hostPort        = '5433'
            volumeName      = 'sqlharness-pg-data'
            volumeExists    = $true
            dbExists        = $true
            ready           = $true
            infoOk          = $true
        }
        $result = Invoke-SetupScript -FakeRoot $fakeRoot -State $state
        $result.ExitCode | Should Be 0

        $callText = ($result.Calls -join "`n")
        if ($callText -match '(^|\s)run\s+--detach') { throw "unexpected docker run in calls: $callText" }
        if ($callText -match '(^|\s)start\s+sqlharness-pg') { throw "unexpected docker start in calls: $callText" }
        if ($callText -match 'volume create') { throw "unexpected volume create in calls: $callText" }
        $callText | Should Match 'inspect'
    }

    It 'starts a compatible stopped container' {
        $state = @{
            containerExists = $true
            running         = $false
            hostPort        = '5433'
            volumeName      = 'sqlharness-pg-data'
            volumeExists    = $true
            dbExists        = $true
            ready           = $true
            infoOk          = $true
        }
        $result = Invoke-SetupScript -FakeRoot $fakeRoot -State $state
        $result.ExitCode | Should Be 0

        $callText = ($result.Calls -join "`n")
        $callText | Should Match 'start sqlharness-pg'
        if ($callText -match '(^|\s)run\s+--detach') { throw "unexpected docker run: $callText" }
        $result.State.running | Should Be $true
    }

    It 'skips restore when pagila exists' {
        $state = @{
            containerExists = $true
            running         = $true
            hostPort        = '5433'
            volumeName      = 'sqlharness-pg-data'
            volumeExists    = $true
            dbExists        = $true
            ready           = $true
            infoOk          = $true
        }
        $result = Invoke-SetupScript -FakeRoot $fakeRoot -State $state
        $result.ExitCode | Should Be 0

        $callText = ($result.Calls -join "`n")
        $callText | Should Match 'pg_database'
        $callText | Should Not Match 'CREATE DATABASE'
        $callText | Should Not Match 'pagila-schema.sql'
        if ($callText -match '(^|\s)cp\s+') { throw "unexpected docker cp: $callText" }
        $result.Output | Should Match 'pagila'
    }

    It 'restores pagila when the database is absent' {
        $state = @{
            containerExists = $true
            running         = $true
            hostPort        = '5433'
            volumeName      = 'sqlharness-pg-data'
            volumeExists    = $true
            dbExists        = $false
            ready           = $true
            infoOk          = $true
        }
        $result = Invoke-SetupScript -FakeRoot $fakeRoot -State $state
        if ($result.ExitCode -ne 0) {
            Write-Host "DEBUG output: $($result.Output)"
            Write-Host "DEBUG calls: $($result.Calls -join ' | ')"
        }
        $result.ExitCode | Should Be 0

        $callText = ($result.Calls -join "`n")
        $callText | Should Match 'pg_database'
        $callText | Should Match 'CREATE DATABASE'
        $callText | Should Match 'pagila-schema.sql'
        $callText | Should Match 'pagila-data.sql'
        $callText | Should Match '(^|\s)cp\s+'
        $result.State.dbExists | Should Be $true
    }

    It 'drops incomplete pagila when dump restore fails after CREATE DATABASE' {
        $state = @{
            containerExists = $true
            running         = $true
            hostPort        = '5433'
            volumeName      = 'sqlharness-pg-data'
            volumeExists    = $true
            dbExists        = $false
            ready           = $true
            infoOk          = $true
            restoreOk       = $false
        }
        $result = Invoke-SetupScript -FakeRoot $fakeRoot -State $state
        $result.ExitCode | Should Not Be 0

        $callText = ($result.Calls -join "`n")
        $callText | Should Match 'CREATE DATABASE'
        $callText | Should Match 'pagila-schema.sql'
        $callText | Should Match 'DROP DATABASE'
        $result.State.dbExists | Should Be $false
        $result.Output.Contains($script:FixturePassword) | Should Be $false
    }

    It 'script text contains DROP DATABASE cleanup for failed restore' {
        $text = Get-Content -Raw -Path $script:SetupScript
        $text | Should Match 'DROP DATABASE IF EXISTS'
        $text | Should Match 'Remove-IncompletePagilaDatabase'
        $text | Should Match '\$databaseCreated'
        $text | Should Match '\$restoreCompleted'
    }

    It 'stops on a conflicting port mapping' {
        $state = @{
            containerExists = $true
            running         = $true
            hostPort        = '5432'
            volumeName      = 'sqlharness-pg-data'
            volumeExists    = $true
            dbExists        = $true
            ready           = $true
            infoOk          = $true
        }
        $result = Invoke-SetupScript -FakeRoot $fakeRoot -State $state
        $result.ExitCode | Should Not Be 0
        $result.Output | Should Match 'STOP:'
        $result.Output | Should Match 'port'

        $callText = ($result.Calls -join "`n")
        if ($callText -match '(^|\s)run\s+--detach') { throw "unexpected docker run: $callText" }
        if ($callText -match '(^|\s)start\s+sqlharness-pg') { throw "unexpected docker start: $callText" }
        if ($callText -match 'volume create') { throw "unexpected volume create: $callText" }
    }

    It 'stops on a conflicting volume mount' {
        $state = @{
            containerExists = $true
            running         = $true
            hostPort        = '5433'
            volumeName      = 'other-volume'
            volumeExists    = $true
            dbExists        = $true
            ready           = $true
            infoOk          = $true
        }
        $result = Invoke-SetupScript -FakeRoot $fakeRoot -State $state
        $result.ExitCode | Should Not Be 0
        $result.Output | Should Match 'STOP:'
        $result.Output | Should Match 'volume'

        $callText = ($result.Calls -join "`n")
        if ($callText -match '(^|\s)run\s+--detach') { throw "unexpected docker run: $callText" }
        if ($callText -match '(^|\s)start\s+sqlharness-pg') { throw "unexpected docker start: $callText" }
    }

    It 'does not print the password' {
        $state = @{
            containerExists = $true
            running         = $true
            hostPort        = '5433'
            volumeName      = 'sqlharness-pg-data'
            volumeExists    = $true
            dbExists        = $true
            ready           = $true
            infoOk          = $true
        }
        $result = Invoke-SetupScript -FakeRoot $fakeRoot -State $state -Password $script:FixturePassword
        $result.ExitCode | Should Be 0
        $result.Output.Contains($script:FixturePassword) | Should Be $false
    }
}
