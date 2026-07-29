# Pester 3.4-compatible unit tests for setup-local-adventureworks.ps1
# Uses a fake docker on PATH; never talks to a real Docker daemon.

$ErrorActionPreference = 'Stop'

$script:RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$script:SetupScript = Join-Path $script:RepoRoot 'scripts\setup-local-adventureworks.ps1'
$script:FixturePassword = 'FixtureSecretPw_NotReal99'

function New-FakeDockerRoot {
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ("sqlharness-fake-docker-" + [Guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $root 'logs') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $root 'state') -Force | Out-Null

    # Pure-batch fake docker: fast startup, no nested pwsh per call.
    # State flags are empty files under state\; calls logged to logs\calls.log
    $cmdPath = Join-Path $root 'docker.cmd'
    # Goto-based batch (no shift-in-parens). FAKE_DOCKER_ROOT points at this root.
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
if exist "%ST%\container.exists" echo sqlharness-sql
exit /b 0
:cmd_inspect
if not exist "%ST%\container.exists" (echo Error: No such object: sqlharness-sql& exit /b 1)
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
echo {"1433/tcp":[{"HostIp":"","HostPort":"%HOSTPORT%"}]}
exit /b 0
:inspect_mounts
set /p VOLNAME=<"%ST%\volumeName.txt"
echo [{"Type":"volume","Name":"%VOLNAME%","Source":"/var/lib/docker/volumes/%VOLNAME%/_data","Destination":"/var/opt/mssql","Driver":"local","Mode":"z","RW":true,"Propagation":""}]
exit /b 0
:inspect_running
if exist "%ST%\container.running" (echo true) else (echo false)
exit /b 0
:cmd_volume
if /I "%~2"=="inspect" goto vol_inspect
if /I "%~2"=="create" goto vol_create
exit /b 1
:vol_inspect
if exist "%ST%\volume.exists" (echo [{"Name":"sqlharness-sql-data"}]& exit /b 0)
echo Error: No such volume: sqlharness-sql-data
exit /b 1
:vol_create
type nul > "%ST%\volume.exists"
echo sqlharness-sql-data
exit /b 0
:cmd_run
type nul > "%ST%\container.exists"
type nul > "%ST%\container.running"
type nul > "%ST%\volume.exists"
echo fakecontainerid
exit /b 0
:cmd_start
type nul > "%ST%\container.running"
echo sqlharness-sql
exit /b 0
:cmd_exec
findstr /C:"SELECT 1" "%LAST%" >nul
if not errorlevel 1 goto exec_select1
findstr /C:"DB_ID" "%LAST%" >nul
if not errorlevel 1 goto exec_dbid
findstr /C:"FILELISTONLY" "%LAST%" >nul
if not errorlevel 1 goto exec_filelist
findstr /C:"RESTORE DATABASE" "%LAST%" >nul
if not errorlevel 1 goto exec_restore
exit /b 0
:exec_select1
if exist "%ST%\ready.fail" (echo Sqlcmd: Error: Microsoft ODBC Driver& exit /b 1)
echo 1
exit /b 0
:exec_dbid
if exist "%ST%\db.exists" (echo 5& exit /b 0)
echo NULL
exit /b 0
:exec_filelist
if exist "%ST%\filelist.bad" (echo LogicalName PhysicalName Type& echo OnlyData C:\x.mdf D& exit /b 0)
echo LogicalName         PhysicalName  Type FileGroupName
echo AdventureWorks2022  C:\aw.mdf     D    PRIMARY
echo AdventureWorks2022_log C:\aw.ldf  L    NULL
exit /b 0
:exec_restore
type nul > "%ST%\db.exists"
echo RESTORE DATABASE successfully processed
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
    $fileListOk = if ($State.ContainsKey('fileListOk')) { [bool]$State.fileListOk } else { $true }
    $hostPort = if ($State.hostPort) { [string]$State.hostPort } else { '14335' }
    $volumeName = if ($State.volumeName) { [string]$State.volumeName } else { 'sqlharness-sql-data' }

    Set-Content -Path (Join-Path $st 'hostPort.txt') -Value $hostPort -Encoding ascii -NoNewline
    Set-Content -Path (Join-Path $st 'volumeName.txt') -Value $volumeName -Encoding ascii -NoNewline

    if ($containerExists) { New-Item -ItemType File -Path (Join-Path $st 'container.exists') -Force | Out-Null }
    if ($running) { New-Item -ItemType File -Path (Join-Path $st 'container.running') -Force | Out-Null }
    if ($volumeExists) { New-Item -ItemType File -Path (Join-Path $st 'volume.exists') -Force | Out-Null }
    if ($dbExists) { New-Item -ItemType File -Path (Join-Path $st 'db.exists') -Force | Out-Null }
    if (-not $ready) { New-Item -ItemType File -Path (Join-Path $st 'ready.fail') -Force | Out-Null }
    if (-not $infoOk) { New-Item -ItemType File -Path (Join-Path $st 'info.fail') -Force | Out-Null }
    if (-not $fileListOk) { New-Item -ItemType File -Path (Join-Path $st 'filelist.bad') -Force | Out-Null }
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
    $previousPassword = $env:SQLHARNESS_PLAYGROUND_PASSWORD
    $previousMssql = $env:MSSQL_SA_PASSWORD
    $previousPoll = $env:SQLHARNESS_PLAYGROUND_READY_POLL_SECONDS

    try {
        $env:PATH = $FakeRoot + [IO.Path]::PathSeparator + $previousPath
        $env:SQLHARNESS_PLAYGROUND_READY_POLL_SECONDS = '0'
        if ($ClearPassword) {
            Remove-Item Env:SQLHARNESS_PLAYGROUND_PASSWORD -ErrorAction SilentlyContinue
        }
        else {
            $env:SQLHARNESS_PLAYGROUND_PASSWORD = $Password
        }

        $outputLines = New-Object System.Collections.Generic.List[string]
        $exitCode = 0
        $threw = $null

        # In-process invocation so fake docker.cmd on PATH is used without nested pwsh for the script.
        $oldEap = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Stop'
            # Clear leftover native exit codes from prior docker probes.
            $global:LASTEXITCODE = 0
            $raw = & $script:SetupScript *>&1
            foreach ($line in @($raw)) {
                $outputLines.Add([string]$line) | Out-Null
            }
            # Success path returns; treat as 0 even if docker left LASTEXITCODE set.
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
        if ($null -eq $previousPassword) {
            Remove-Item Env:SQLHARNESS_PLAYGROUND_PASSWORD -ErrorAction SilentlyContinue
        }
        else {
            $env:SQLHARNESS_PLAYGROUND_PASSWORD = $previousPassword
        }
        if ($null -eq $previousMssql) {
            Remove-Item Env:MSSQL_SA_PASSWORD -ErrorAction SilentlyContinue
        }
        else {
            $env:MSSQL_SA_PASSWORD = $previousMssql
        }
        if ($null -eq $previousPoll) {
            Remove-Item Env:SQLHARNESS_PLAYGROUND_READY_POLL_SECONDS -ErrorAction SilentlyContinue
        }
        else {
            $env:SQLHARNESS_PLAYGROUND_READY_POLL_SECONDS = $previousPoll
        }
    }
}

Describe 'setup-local-adventureworks.ps1' {
    $fakeRoot = $null

    BeforeEach {
        $fakeRoot = New-FakeDockerRoot
    }

    AfterEach {
        if ($fakeRoot -and (Test-Path $fakeRoot)) {
            Remove-Item -Recurse -Force $fakeRoot -ErrorAction SilentlyContinue
        }
    }

    It 'rejects a blank SQLHARNESS_PLAYGROUND_PASSWORD' {
        Test-Path $script:SetupScript | Should Be $true

        $result = Invoke-SetupScript -FakeRoot $fakeRoot -ClearPassword
        $combined = $result.Output
        $combined | Should Match 'SQLHARNESS_PLAYGROUND_PASSWORD'
        $result.ExitCode | Should Not Be 0
        @($result.Calls).Count | Should Be 0
    }

    It 'creates fixed resources when they are absent' {
        $state = @{
            containerExists = $false
            running         = $false
            hostPort        = '14335'
            volumeName      = 'sqlharness-sql-data'
            volumeExists    = $false
            dbExists        = $true   # skip download/restore in unit tests
            ready           = $true
            infoOk          = $true
            fileListOk      = $true
        }
        $result = Invoke-SetupScript -FakeRoot $fakeRoot -State $state
        if ($result.ExitCode -ne 0) {
            Write-Host "DEBUG output: $($result.Output)"
            Write-Host "DEBUG calls: $($result.Calls -join ' | ')"
        }
        $result.ExitCode | Should Be 0

        $callText = ($result.Calls -join "`n")
        $callText | Should Match 'volume create sqlharness-sql-data'
        $callText | Should Match '\brun\b'
        $callText | Should Match 'sqlharness-sql'
        $callText | Should Match '14335:1433'
        $callText | Should Match 'sqlharness-sql-data:/var/opt/mssql'
        $callText | Should Match 'mcr.microsoft.com/mssql/server:2022-latest'
        $result.Output | Should Not Match ([regex]::Escape($script:FixturePassword))
    }

    It 'reuses a compatible running container' {
        $state = @{
            containerExists = $true
            running         = $true
            hostPort        = '14335'
            volumeName      = 'sqlharness-sql-data'
            volumeExists    = $true
            dbExists        = $true
            ready           = $true
            infoOk          = $true
            fileListOk      = $true
        }
        $result = Invoke-SetupScript -FakeRoot $fakeRoot -State $state
        $result.ExitCode | Should Be 0

        $callText = ($result.Calls -join "`n")
        # Must not create a new container or start; may inspect/exec only
        ($callText -match '(?m)^\s*run\b' -or $callText -match '(?m)^run ') | Should Be $false
        # docker.cmd logs "run --detach ..." without leading spaces typically
        if ($callText -match '(^|\s)run\s+--detach') { throw "unexpected docker run in calls: $callText" }
        if ($callText -match '(^|\s)start\s+sqlharness-sql') { throw "unexpected docker start in calls: $callText" }
        if ($callText -match 'volume create') { throw "unexpected volume create in calls: $callText" }
        $callText | Should Match 'inspect'
    }

    It 'starts a compatible stopped container' {
        $state = @{
            containerExists = $true
            running         = $false
            hostPort        = '14335'
            volumeName      = 'sqlharness-sql-data'
            volumeExists    = $true
            dbExists        = $true
            ready           = $true
            infoOk          = $true
            fileListOk      = $true
        }
        $result = Invoke-SetupScript -FakeRoot $fakeRoot -State $state
        $result.ExitCode | Should Be 0

        $callText = ($result.Calls -join "`n")
        $callText | Should Match 'start sqlharness-sql'
        if ($callText -match '(^|\s)run\s+--detach') { throw "unexpected docker run: $callText" }
        $result.State.running | Should Be $true
    }

    It 'skips restore when AdventureWorks2022 exists' {
        $state = @{
            containerExists = $true
            running         = $true
            hostPort        = '14335'
            volumeName      = 'sqlharness-sql-data'
            volumeExists    = $true
            dbExists        = $true
            ready           = $true
            infoOk          = $true
            fileListOk      = $true
        }
        $result = Invoke-SetupScript -FakeRoot $fakeRoot -State $state
        $result.ExitCode | Should Be 0

        $callText = ($result.Calls -join "`n")
        $callText | Should Match 'DB_ID'
        $callText | Should Not Match 'FILELISTONLY'
        $callText | Should Not Match 'RESTORE DATABASE'
        # docker cp should not appear
        if ($callText -match '(^|\s)cp\s+') { throw "unexpected docker cp: $callText" }
        $result.Output | Should Match 'AdventureWorks2022'
    }

    It 'stops on a conflicting port mapping' {
        $state = @{
            containerExists = $true
            running         = $true
            hostPort        = '14334'
            volumeName      = 'sqlharness-sql-data'
            volumeExists    = $true
            dbExists        = $true
            ready           = $true
            infoOk          = $true
            fileListOk      = $true
        }
        $result = Invoke-SetupScript -FakeRoot $fakeRoot -State $state
        $result.ExitCode | Should Not Be 0
        $result.Output | Should Match 'STOP:'
        $result.Output | Should Match 'port'

        $callText = ($result.Calls -join "`n")
        if ($callText -match '(^|\s)run\s+--detach') { throw "unexpected docker run: $callText" }
        if ($callText -match '(^|\s)start\s+sqlharness-sql') { throw "unexpected docker start: $callText" }
        if ($callText -match 'volume create') { throw "unexpected volume create: $callText" }
    }

    It 'stops on a conflicting volume mount' {
        $state = @{
            containerExists = $true
            running         = $true
            hostPort        = '14335'
            volumeName      = 'other-volume'
            volumeExists    = $true
            dbExists        = $true
            ready           = $true
            infoOk          = $true
            fileListOk      = $true
        }
        $result = Invoke-SetupScript -FakeRoot $fakeRoot -State $state
        $result.ExitCode | Should Not Be 0
        $result.Output | Should Match 'STOP:'
        $result.Output | Should Match 'volume'

        $callText = ($result.Calls -join "`n")
        if ($callText -match '(^|\s)run\s+--detach') { throw "unexpected docker run: $callText" }
        if ($callText -match '(^|\s)start\s+sqlharness-sql') { throw "unexpected docker start: $callText" }
    }

    It 'does not print the password' {
        $state = @{
            containerExists = $true
            running         = $true
            hostPort        = '14335'
            volumeName      = 'sqlharness-sql-data'
            volumeExists    = $true
            dbExists        = $true
            ready           = $true
            infoOk          = $true
            fileListOk      = $true
        }
        $result = Invoke-SetupScript -FakeRoot $fakeRoot -State $state -Password $script:FixturePassword
        $result.ExitCode | Should Be 0
        $result.Output.Contains($script:FixturePassword) | Should Be $false
    }
}
