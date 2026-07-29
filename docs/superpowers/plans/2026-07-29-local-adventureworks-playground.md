# Optional Local AdventureWorks Playground Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a reproducible but optional local SQL Server 2022/AdventureWorks2022 environment for manual SQLHarness work and opt-in integration tests.

**Architecture:** A repository-owned PowerShell script creates or reuses fixed, compatible Docker resources and restores AdventureWorks2022 only when absent. Machine-local Docker state, target profiles, passwords, and connection strings stay outside the repository; normal builds and tests remain database-independent.

**Tech Stack:** PowerShell 7, Docker Desktop, SQL Server 2022 Developer, AdventureWorks2022, SQLHarness, xUnit.

## Global Constraints

- Read `docs/superpowers/specs/2026-07-29-local-adventureworks-playground-design.md` before editing.
- Do not modify, stop, recreate, or reuse `civiclens-sql`, port `14334`, BSI profiles, or another project's resources.
- Use exactly container `sqlharness-sql`, volume `sqlharness-sql-data`, host port `14335`, database `AdventureWorks2022`, and profile `local-playground`.
- Treat the playground as optional development infrastructure, never as a runtime, build, packaging, or ordinary unit-test dependency.
- Do not edit `~/.sqlharness/targets.json`; document an exact fragment for the user to merge.
- Do not write passwords, connection strings, backups, plans, or comparison artifacts into the repository.
- Never delete or recreate an existing Docker resource to resolve a mismatch. Stop and report the mismatch.
- Do not bypass any SQLHarness safety rejection.
- Make one final commit only after all available verification passes. If Docker or network verification is unavailable, do not claim the live environment is verified.

---

### Task 1: Add a testable, fail-closed bootstrap script

**Files:**
- Create: `scripts/setup-local-adventureworks.ps1`
- Create: `tests/scripts/setup-local-adventureworks.Tests.ps1`

**Interfaces:**
- Consumes: non-empty process variable `SQLHARNESS_PLAYGROUND_PASSWORD`, `docker`, and outbound HTTPS for the official backup.
- Produces: compatible local resources `sqlharness-sql`, `sqlharness-sql-data`, and `AdventureWorks2022`; exit `0` for both first-run and already-ready states.

- [ ] **Step 1: Check the existing test conventions and prerequisites**

Run:

```powershell
Get-Command pwsh
Get-Command docker
Get-Module -ListAvailable Pester | Select-Object Name, Version, Path
Get-ChildItem scripts, tests -Force
```

Expected: record whether PowerShell 7, Docker, and Pester are available. Missing
Docker is not a blocker for parser/unit work, but is a STOP condition for live
verification. If the repository has no PowerShell-test convention and Pester is not
installed, do not install it; perform parser validation and live behavior checks
instead.

- [ ] **Step 2: Write failing Pester coverage when Pester is already available**

Create tests that invoke the script with a fake `docker` executable earlier on
`PATH`. Cover these observable cases:

```powershell
Describe 'setup-local-adventureworks.ps1' {
    It 'rejects a blank SQLHARNESS_PLAYGROUND_PASSWORD' { }
    It 'creates fixed resources when they are absent' { }
    It 'reuses a compatible running container' { }
    It 'starts a compatible stopped container' { }
    It 'skips restore when AdventureWorks2022 exists' { }
    It 'stops on a conflicting port mapping' { }
    It 'stops on a conflicting volume mount' { }
    It 'does not print the password' { }
}
```

The fake executable must log argument arrays to a temporary directory and return
fixture output for `docker ps`, `docker inspect`, `docker volume inspect`,
`docker exec`, and `docker cp`. Do not call the real Docker daemon from these tests.

- [ ] **Step 3: Run the focused tests and confirm the red state**

Run:

```powershell
Invoke-Pester .\tests\scripts\setup-local-adventureworks.Tests.ps1 -Output Detailed
```

Expected: FAIL because `scripts/setup-local-adventureworks.ps1` does not exist.
If Step 1 established that Pester is unavailable, record `SKIP: Pester unavailable`
and continue to parser and live verification without adding a Pester test file.

- [ ] **Step 4: Implement prerequisite and conflict checks**

Create `scripts/setup-local-adventureworks.ps1` with:

```powershell
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$containerName = 'sqlharness-sql'
$volumeName = 'sqlharness-sql-data'
$hostPort = 14335
$databaseName = 'AdventureWorks2022'
$image = 'mcr.microsoft.com/mssql/server:2022-latest'

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
```

Use `docker ps -a --filter "name=^/$containerName$" --format '{{.Names}}'` to detect
the exact container. For an existing container, inspect:

```powershell
docker inspect $containerName --format '{{json .HostConfig.PortBindings}}'
docker inspect $containerName --format '{{json .Mounts}}'
docker inspect $containerName --format '{{.State.Running}}'
```

Parse returned JSON with `ConvertFrom-Json`. Require container port `1433/tcp` to
map to host port `14335` and `/var/opt/mssql` to use named volume
`sqlharness-sql-data`. Throw a message containing `STOP:` and the mismatched field
before any mutation.

When the container is absent, reject a listening host port:

```powershell
if (Get-NetTCPConnection -LocalPort $hostPort -State Listen -ErrorAction SilentlyContinue) {
    throw "STOP: host port $hostPort is already in use."
}
```

Create the volume only if `docker volume inspect $volumeName` reports it absent.
Create the container by temporarily mapping the playground password to the variable
expected by the SQL Server image:

```powershell
$previousSqlPassword = $env:MSSQL_SA_PASSWORD
try {
    $env:MSSQL_SA_PASSWORD = $env:SQLHARNESS_PLAYGROUND_PASSWORD
    docker run --detach --name $containerName `
        --publish "${hostPort}:1433" `
        --volume "${volumeName}:/var/opt/mssql" `
        --env ACCEPT_EULA=Y `
        --env MSSQL_PID=Developer `
        --env MSSQL_SA_PASSWORD `
        $image | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Failed to create sqlharness-sql.' }
}
finally {
    if ($null -eq $previousSqlPassword) {
        Remove-Item Env:MSSQL_SA_PASSWORD -ErrorAction SilentlyContinue
    }
    else {
        $env:MSSQL_SA_PASSWORD = $previousSqlPassword
    }
}
```

This fixes the old plan's mismatch between `SQLHARNESS_PLAYGROUND_PASSWORD` and
`MSSQL_SA_PASSWORD`.

- [ ] **Step 5: Implement readiness and idempotent restore**

Start a compatible stopped container with `docker start sqlharness-sql`. Poll for no
more than three minutes:

```powershell
docker exec `
    --env "SQLCMDPASSWORD=$($env:SQLHARNESS_PLAYGROUND_PASSWORD)" `
    $containerName `
    /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -Q 'SELECT 1' -b
```

Do not include the password in log messages. Query database existence using
`DB_ID(N'AdventureWorks2022')`. If present, print a neutral success message and skip
download and restore.

If absent:

1. Create a unique directory with
   `Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetRandomFileName())`.
2. Download
   `https://github.com/Microsoft/sql-server-samples/releases/download/adventureworks/AdventureWorks2022.bak`
   into that directory.
3. Copy it to
   `/var/opt/mssql/backup/AdventureWorks2022.bak`.
4. Run `RESTORE FILELISTONLY` and obtain the logical data and log names rather than
   hard-coding them.
5. Safely quote those discovered names and restore to
   `/var/opt/mssql/data/AdventureWorks2022.mdf` and
   `/var/opt/mssql/data/AdventureWorks2022_log.ldf`.
6. In `finally`, remove the host temporary directory. After successful restore,
   remove the copied container backup.

Require exactly one data file and one log file. Otherwise throw
`STOP: expected one AdventureWorks2022 data file and one log file.` before restore.

- [ ] **Step 6: Validate parser and focused behavior**

Run:

```powershell
$errors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path .\scripts\setup-local-adventureworks.ps1),
    [ref]$null,
    [ref]$errors
) | Out-Null
if ($errors.Count -ne 0) { $errors | Format-List; exit 1 }
```

Expected: exit `0`, no parser errors.

When Pester was available in Step 1, also run:

```powershell
Invoke-Pester .\tests\scripts\setup-local-adventureworks.Tests.ps1 -Output Detailed
```

Expected: PASS for all cases, and the captured output does not contain the fixture
password.

---

### Task 2: Document the optional profile and integration-test boundary

**Files:**
- Modify: `README.md`

**Interfaces:**
- Consumes: the bootstrap script and existing SQLHarness target/profile behavior.
- Produces: exact opt-in setup, profile, smoke-test, integration-test, security, and cleanup instructions.

- [ ] **Step 1: Add an optional local playground section**

Add a section titled `Optional local AdventureWorks playground`. State explicitly:

- SQLHarness does not require a database to build, install, start, or run offline
  commands such as `plan` and `gain`;
- this environment exists only for development, manual experiments, and opt-in SQL
  Server integration tests;
- Docker resources and secrets remain local;
- Docker stores the SQL Server bootstrap password in local container metadata, so
  the playground password must be development-only and unique.

Document setup:

```powershell
$env:SQLHARNESS_PLAYGROUND_PASSWORD = Read-Host 'Local playground password'
.\scripts\setup-local-adventureworks.ps1
```

- [ ] **Step 2: Document conscious profile merging**

Tell the user to merge, not replace, this entry in
`~/.sqlharness/targets.json`:

```json
{
  "local-playground": {
    "server": "localhost,14335",
    "database": "AdventureWorks2022",
    "vars": {},
    "auth": "sql",
    "sqlUser": "sa",
    "passwordEnvVar": "SQLHARNESS_PLAYGROUND_PASSWORD",
    "trustServerCertificate": true
  }
}
```

Warn that an existing `local-playground` entry with different values must be reviewed
manually. The bootstrap script never edits this file.

- [ ] **Step 3: Document read-only CLI smoke checks**

Use the installed command first:

```powershell
sqlharness schema local-playground --json
```

Create the local smoke query outside tracked repository paths:

```powershell
$smokeQuery = Join-Path $env:TEMP 'sqlharness-playground-smoke.sql'
@'
SELECT TOP (10) p.ProductID, p.Name, p.ListPrice
FROM Production.Product AS p
WHERE p.ListPrice > @minimumPrice
ORDER BY p.ListPrice DESC;
'@ | Set-Content $smokeQuery -Encoding utf8

sqlharness measure local-playground `
    --query $smokeQuery `
    --param minimumPrice:decimal=100 `
    --repeat 2 `
    --json
```

Expected: both commands exit `0`; schema identifies `AdventureWorks2022`, and measure
returns bounded JSON output.

- [ ] **Step 4: Document the independent integration-test configuration**

State that tests never load `~/.sqlharness/targets.json`. Construct the opt-in
connection string only in the current process:

```powershell
$env:SQLHARNESS_INTEGRATION_CONNECTION_STRING = `
    "Server=localhost,14335;Database=AdventureWorks2022;User ID=sa;Password=$($env:SQLHARNESS_PLAYGROUND_PASSWORD);TrustServerCertificate=True"

dotnet test .\tests\SqlHarness.Tests `
    --filter Category=SqlServerIntegration `
    --no-restore
```

Also document the clean skip check:

```powershell
Remove-Item Env:SQLHARNESS_INTEGRATION_CONNECTION_STRING -ErrorAction SilentlyContinue
dotnet test .\tests\SqlHarness.Tests `
    --filter Category=SqlServerIntegration `
    --no-restore
```

Expected: configured tests PASS; unconfigured tests report skipped tests and zero
failures.

- [ ] **Step 5: Document non-destructive lifecycle commands**

Document:

```powershell
docker stop sqlharness-sql
docker start sqlharness-sql
docker restart sqlharness-sql
```

Do not provide automated removal commands in the setup path. Explain that deleting
the container or volume destroys local state and must be a separate, explicit user
action.

- [ ] **Step 6: Review documentation against current CLI**

Run:

```powershell
sqlharness --help
sqlharness schema --help
sqlharness measure --help
rg -n "local-playground|SQLHARNESS_PLAYGROUND_PASSWORD|SQLHARNESS_INTEGRATION_CONNECTION_STRING|14335|AdventureWorks2022" README.md
```

Expected: documented option names match current help, both configuration paths are
present, and README never presents the playground as required infrastructure.

---

### Task 3: Perform final static and live verification

**Files:**
- Verify: `scripts/setup-local-adventureworks.ps1`
- Verify: `tests/scripts/setup-local-adventureworks.Tests.ps1` when created
- Verify: `README.md`

**Interfaces:**
- Consumes: Tasks 1 and 2.
- Produces: evidence suitable for review without exposing credentials.

- [ ] **Step 1: Run repository tests**

Run:

```powershell
dotnet test .\tests\SqlHarness.Tests
```

Expected: PASS. Tests that require
`SQLHARNESS_INTEGRATION_CONNECTION_STRING` may be skipped when it is absent.

- [ ] **Step 2: Run first and second bootstrap checks**

Only when Docker is available, port `14335` is free or already owned by a compatible
`sqlharness-sql`, and the user has supplied
`SQLHARNESS_PLAYGROUND_PASSWORD`, run:

```powershell
.\scripts\setup-local-adventureworks.ps1
.\scripts\setup-local-adventureworks.ps1
```

Expected: both exit `0`; the second run reuses the container and skips restore.
Do not invent or persist a password. If the password is not already supplied, record
live setup as `NOT RUN: password input required` rather than prompting inside an
unattended agent run.

- [ ] **Step 3: Verify persistence and fixed identity**

Run:

```powershell
docker restart sqlharness-sql | Out-Null
docker ps --filter 'name=^/sqlharness-sql$' --format '{{.Names}} {{.Status}} {{.Ports}}'
sqlharness schema local-playground --json
```

Expected: the container publishes `14335->1433`, becomes healthy, and schema exits
`0` for `AdventureWorks2022`.

- [ ] **Step 4: Verify configured and unconfigured integration behavior**

Run the README commands for:

1. `Category=SqlServerIntegration` with
   `SQLHARNESS_INTEGRATION_CONNECTION_STRING` set;
2. the same filter after removing the variable.

Expected: configured PASS; unconfigured skip with zero failures.

- [ ] **Step 5: Audit repository cleanliness and scope**

Run:

```powershell
git status --short
git diff --check
git diff -- README.md scripts/setup-local-adventureworks.ps1 tests/scripts/setup-local-adventureworks.Tests.ps1
rg -n -i "password=|AdventureWorks2022\.bak|MSSQL_SA_PASSWORD=" README.md scripts tests
```

Expected: only intended source, test, and documentation files are changed; no backup,
literal password, connection string with a literal password, `.sqlplan`, comparison
artifact, or user profile is present. The `rg` results may include documented
variable names but no assigned secret.

- [ ] **Step 6: Commit once after verification**

If all static verification passes and the result accurately labels any unavailable
live checks, run:

```powershell
git add README.md scripts/setup-local-adventureworks.ps1
if (Test-Path .\tests\scripts\setup-local-adventureworks.Tests.ps1) {
    git add tests/scripts/setup-local-adventureworks.Tests.ps1
}
git commit -m "feat: add optional AdventureWorks playground setup"
```

Expected: one commit containing only the optional playground implementation,
documentation, and tests. Do not push unless the user explicitly requests it.
