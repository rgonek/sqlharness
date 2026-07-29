# Optional Local AdventureWorks Playground Design

## Goal

Provide a reproducible, optional SQL Server environment for SQLHarness development,
manual CLI experiments, and opt-in integration tests. SQLHarness itself must remain
fully usable without this environment.

## Scope Boundary

The repository owns only:

- an idempotent PowerShell bootstrap script;
- documentation for the optional environment;
- opt-in integration-test instructions.

The developer workstation owns:

- Docker container `sqlharness-sql`;
- Docker volume `sqlharness-sql-data`;
- restored database `AdventureWorks2022`;
- user-local profile `local-playground`;
- passwords and connection strings.

No database, backup, Docker state, user profile, or secret is committed to the
repository. Normal builds, unit tests, packaging, and CLI startup do not create or
require a database.

## Architecture

Docker runs SQL Server 2022 Developer Edition in a dedicated container named
`sqlharness-sql`, publishing SQL Server on `localhost:14335`. The named volume
`sqlharness-sql-data` retains system databases and `AdventureWorks2022` across
container restarts and recreation.

The setup script creates missing resources and reuses compatible existing resources.
It restores the official `AdventureWorks2022` backup only when that database is
absent. It stops without changing anything when an existing container, port binding,
or volume conflicts with the required configuration.

Two consumers remain deliberately separate:

1. Manual SQLHarness commands use a closed user-local profile named
   `local-playground`.
2. Opt-in integration tests use only
   `SQLHARNESS_INTEGRATION_CONNECTION_STRING` and never load
   `~/.sqlharness/targets.json`.

## Repository Components

| Component | Responsibility |
| --- | --- |
| `scripts/setup-local-adventureworks.ps1` | Validate prerequisites, create or reuse compatible Docker resources, and restore AdventureWorks2022 when absent. |
| `README.md` development section | Explain optional setup, local profile configuration, integration-test configuration, verification, and cleanup. |

The bootstrap script does not edit `~/.sqlharness/targets.json`. The README provides
the exact profile fragment so the developer can merge it consciously without risking
existing profiles.

## Fixed Local Resource Names

- Container: `sqlharness-sql`
- Volume: `sqlharness-sql-data`
- Host endpoint: `localhost,14335`
- Container endpoint: port `1433`
- Database: `AdventureWorks2022`
- Closed SQLHarness profile: `local-playground`
- Password variable: `SQLHARNESS_PLAYGROUND_PASSWORD`
- Integration-test variable: `SQLHARNESS_INTEGRATION_CONNECTION_STRING`

The existing CivicLens container and its host port `14334` are out of scope and must
not be inspected beyond a name/port collision check, modified, stopped, or reused.
Existing BSI profiles are also out of scope.

## Bootstrap Behavior

The script requires a non-empty `SQLHARNESS_PLAYGROUND_PASSWORD` and verifies that
`docker` is available. It then follows these rules:

1. If `sqlharness-sql` does not exist, port `14335` must be free. The script creates
   `sqlharness-sql-data` when needed and creates the container with the fixed
   configuration.
2. If `sqlharness-sql` exists, its published port and mounted volume must match the
   fixed configuration. A mismatch is a STOP condition; the script does not recreate
   or mutate it.
3. A compatible stopped container is started. A compatible running container is
   reused.
4. The script waits up to three minutes for SQL Server readiness.
5. If `AdventureWorks2022` exists, restore is skipped.
6. Otherwise, the script downloads the official backup to a temporary local path,
   copies it into the container, reads the backup logical file names using
   `RESTORE FILELISTONLY`, and restores the database using those discovered names.
7. Temporary host and container backup files are removed after a successful restore.

The script must not print the password or a connection string. Docker necessarily
stores the SQL Server bootstrap environment in local container metadata; the README
must describe that local-machine exposure accurately rather than claiming the secret
exists only in process memory.

## Profile and Test Configuration

The user-local `local-playground` profile resolves exactly to
`localhost,14335` / `AdventureWorks2022`, accepts no variables, uses SQL
authentication, and reads its password from `SQLHARNESS_PLAYGROUND_PASSWORD`.

Integration tests receive an explicit connection string through
`SQLHARNESS_INTEGRATION_CONNECTION_STRING`. Documentation constructs that value in
the current PowerShell process from `SQLHARNESS_PLAYGROUND_PASSWORD`; it is never
written to the repository or loaded from the profile.

## Verification

Implementation is complete only when all of the following pass:

1. PowerShell parser validation succeeds for the bootstrap script.
2. First setup creates a healthy container, persistent volume, and database.
3. A second setup run succeeds without recreating or restoring resources.
4. Restarting the container preserves `AdventureWorks2022`.
5. `sqlharness schema local-playground --json` exits `0` and resolves
   `AdventureWorks2022`.
6. The documented bounded `measure` smoke command exits `0`.
7. Integration tests skip cleanly without
   `SQLHARNESS_INTEGRATION_CONNECTION_STRING`.
8. Integration tests pass when the variable targets this isolated database.
9. `git status --short` contains no generated backup, secret, target profile, SQL
   plan, or comparison artifact.

## STOP Conditions

The implementing agent must stop and report evidence instead of working around:

- an unavailable Docker daemon;
- occupied host port `14335`;
- an existing `sqlharness-sql` with different ports or mounts;
- an existing `sqlharness-sql-data` that cannot be safely reused;
- missing or blank password input;
- failure to identify the backup's logical data and log files;
- a SQLHarness safety rejection;
- any required modification to CivicLens, BSI, or another project's resources.

## Acceptance Criteria

- The environment is explicitly documented as optional development infrastructure.
- Running normal builds and tests does not provision or require SQL Server.
- Bootstrap is repeatable for absent, stopped, running, and already-restored states.
- Conflicts fail closed without deleting or recreating resources.
- Manual CLI use and integration tests use their documented, separate configuration
  paths.
- No secrets or machine-local state enter the repository.
