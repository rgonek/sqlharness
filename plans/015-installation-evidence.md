# Measurement of the installed CLI and the repository build

Status: this file is the 015/T1–T3 diagnosis. No installation was performed. No code was changed.

## 015/T1

The measurement is from 2026-10-02 on this Windows host. The plan's base, named in `plans/015-installation-alignment.md`, is `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`. That commit is in the repository. `git merge-base --is-ancestor` relative to the measured HEAD exited with code 0, so the base is an ancestor, not that HEAD. `git rev-parse HEAD` before this document returned `ece0d7fcaef90325d20865db639c12b47bc2de4e`. `git status --short` was empty at that time. This is not a measurement on the base from 2026-09-29.

No `targets.json`, profiles, passwords, connection strings or environment variable values were read. `dotnet test` was not run. Nothing was published, copied or used to replace the binary. No commands, stubs or capabilities were added. This step does not record an update procedure.

### Commands

| Command | Code | Stdout | Stderr |
|---|---|---|---|
| `Get-Command sqlharness -All` | no process code; the cmdlet reported no exception | one object | no stream |
| `sqlharness --help` | 0 | 376 B | 0 B |
| `sqlharness --version` | 0 | 7 B | 0 B |
| `dotnet run --project src/SqlHarness.Cli -- --help` | 0 | 1971 B | 0 B |
| `dotnet run --project src/SqlHarness.Cli -- capabilities --json` | 0 | 6956 B | 0 B |

The two `sqlharness` invocations went through `cmd.exe /d /c` in this host's session. The same `--help` and the same `--version` on the path from `Get-Command` gave the same code 0 and the same stdout SHA-256. Stderr was also 0 B. The `dotnet run` working directory is `D:\Dev\sqlharness\.worktrees\plan-015-installation-alignment`. Neither of the two `dotnet run` streams contained an MSBuild log. The repository help stdout begins with `USAGE:`. The capabilities stdout begins with `{`.

### Get-Command

One result. `CommandType` = `Application`. `Name` = `sqlharness.exe`. `Source` and `Path` = `C:\Users\rgone\.local\bin\sqlharness.exe`. `Definition` is the same path. The command object's `Version` = `1.0.0.0`. There was no second application, function or alias.

`1.0.0.0` is a property of the `Get-Command` object. It is not the `--version` stdout text.

### Installed --version

Stdout has 7 bytes: the characters `1.0.0` and a CRLF terminator. SHA-256 `4ca8bbd3c595ee306226c6996b32b8d47a900217c55a7ed0cfc868ee444f34f1`. The version text:

```text
1.0.0
```

The help of that binary advertises `-v, --version`. The `--version` run did not print help. It printed `1.0.0`.

### Installed --help

Stdout has 376 bytes, CRLF terminators, SHA-256 `da240f1cf6099b26b7736c648b440497379501f8d99a5f143ae4461eccb5512c`. The command lines are space-padded and have no descriptions. In the block, each stdout line is between `|`. The spaces before the closing `|` belong to stdout. The `|` character itself does not belong to stdout. The file ends with CRLF after the `snapshot` line; there is no separate blank line after it.

```text
|USAGE:|
|    sqlharness [OPTIONS] <COMMAND>|
||
|OPTIONS:|
|    -h, --help       Prints help information   |
|    -v, --version    Prints version information|
||
|COMMANDS:|
|    query        |
|    measure      |
|    compare      |
|    gain         |
|    plan         |
|    schema       |
|    ping         |
|    counts       |
|    space        |
|    watch        |
|    snapshot     |
```

### Help of the repository build

Stdout has 1971 bytes, CRLF terminators, SHA-256 `77522d1729b33fc99bb9f58d6f4df1e2c3f3088de1588d957b59dd33e1ab8a1c`. The command lines and the description continuations are 80 columns wide, including the padding spaces. The `USAGE` and `OPTIONS` block is not padded to 80. The descriptions are on the same line as the name, or on a continuation indented deeper than four spaces. The same `|` convention as above. The file ends with CRLF after the `mcp` line.

```text
|USAGE:|
|    sqlharness [OPTIONS] <COMMAND>|
||
|OPTIONS:|
|    -h, --help       Prints help information   |
|    -v, --version    Prints version information|
||
|COMMANDS:|
|    query           Run a bounded SQL query passing the static visible-effects  |
|                    check                                                       |
|    measure         Measure query performance across repeated runs              |
|    compare         Compare baseline and candidate performance and results      |
|    gain            Report local output savings estimates                       |
|    plan            Distill a saved execution plan offline                      |
|    artifact        Read safe sections of a saved benchmark artifact            |
|    schema          Inspect database tables, columns, and relations             |
|    ping            Check a database connection and target identity             |
|    counts          Inspect row counts for database tables                      |
|    space           Inspect database file and table storage                     |
|    watch           Poll a bounded query passing the static visible-effects text|
|                    check until a condition is met                              |
|    snapshot        Capture or compare a named query result                     |
|    qstop           Rank SQL Server Query Store consumers                       |
|    indexes         Inspect SQL Server missing-index evidence                   |
|    capabilities    Describe local commands, engines, limits, and output modes  |
|    doctor          Check local installation and profile-file availability      |
|                    without connecting                                          |
|    validate        Classify SQL offline using a closed profile; static         |
|                    visible-effects check only, never connects                  |
|    mcp             Model Context Protocol adapter                              |
```

The first 184 bytes of both helps are identical. The common prefix covers `USAGE`, `OPTIONS`, the `COMMANDS:` header and the start of the `query` line: four spaces, the name `query` and the eight spaces that end that line in the installed help. After that, the installed stdout has CRLF. The repository stdout stays on the same line and appends the description.

### Names in the help

A command name is the first token of a line under `COMMANDS:` whose indentation is exactly four spaces. A description continuation is indented deeper and is not a separate command.

| Name | Installed help | Repository help |
|---|---|---|
| query | yes | yes |
| measure | yes | yes |
| compare | yes | yes |
| gain | yes | yes |
| plan | yes | yes |
| artifact | no | yes |
| schema | yes | yes |
| ping | yes | yes |
| counts | yes | yes |
| space | yes | yes |
| watch | yes | yes |
| snapshot | yes | yes |
| qstop | no | yes |
| indexes | no | yes |
| capabilities | no | yes |
| doctor | no | yes |
| validate | no | yes |
| mcp | no | yes |

In both: `query`, `measure`, `compare`, `gain`, `plan`, `schema`, `ping`, `counts`, `space`, `watch`, `snapshot`. Only in the installed help: none. Only in the repository help: `artifact`, `qstop`, `indexes`, `capabilities`, `doctor`, `validate`, `mcp`.

`capabilities` is in the repository help and is absent from the installed help. `validate` likewise. `mcp` likewise. The repository help does not list `serve` on this page.

Installed order: `query`, `measure`, `compare`, `gain`, `plan`, `schema`, `ping`, `counts`, `space`, `watch`, `snapshot`. Repository order: `query`, `measure`, `compare`, `gain`, `plan`, `artifact`, `schema`, `ping`, `counts`, `space`, `watch`, `snapshot`, `qstop`, `indexes`, `capabilities`, `doctor`, `validate`, `mcp`.

The options `-h, --help` and `-v, --version` are in both helps. Outside the common prefix, the installed help carries no command descriptions. The repository help does carry them.

### capabilities --json

Top-level fields: `version` = `1.0.0`, `buildId` = `1.0.0+ece0d7fcaef90325d20865db639c12b47bc2de4e`, `contractVersion` = `1`, plus `commands`, `engines`, `limits`, `outputModes`, `safetyAnalysis`. In the stdout file the `+` character is written as `\u002B`. After reading the JSON, the `buildId` suffix equals the measured HEAD.

The names in `commands`, in this order: `capabilities`, `doctor`, `validate`, `query`, `measure`, `compare`, `schema`, `ping`, `counts`, `space`, `watch`, `snapshot`, `qstop`, `indexes`, `plan`, `gain`, `artifact`.

Engines: `sqlserver` (`supportsQstop` true, `supportsIndexes` true) and `postgres` (both false). `outputModes`: `text`, `json`, `json-summary`, `agent`. `safetyAnalysis.analysisKind` = `static-visible-effects`, `analysisContractVersion` = `1`, `hiddenEffectsVerified` = false, `objectAndPermissionStatus` = `unknown`.

The set of repository help names and the `commands` set differ by one item. `mcp` is in the repository help and does not occur in this JSON, also as a substring. No name from `commands` is omitted from the repository help. `serve` does not occur in this JSON.

The `version` text equals the installed `--version` text (`1.0.0`). That is not the identity of the binary. The help bodies have a different SHA-256 and a different name list. `buildId` appends the full SHA of that HEAD to that same `1.0.0`.

This body does not contain `C:\Users`, `targets.json`, `.sqlharness`, `password`, `Server=`, `User Id` nor `connectionString`. The words `profile` and `connection` appear only in the descriptions of the contract's commands. The body is pasted in full. The SHA-256 of that stdout is `84dbc47dcd5a4e122ea06117b3946316a242141ba8e4e7545e0e21f34809bef7`, 6956 B, CRLF, terminator after `}`.

```json
{
  "version": "1.0.0",
  "buildId": "1.0.0\u002Bece0d7fcaef90325d20865db639c12b47bc2de4e",
  "contractVersion": 1,
  "commands": [
    {
      "name": "capabilities",
      "description": "Describe commands, engines, limits, and output modes."
    },
    {
      "name": "doctor",
      "description": "Check local SQLHarness installation and profile-file availability."
    },
    {
      "name": "validate",
      "description": "Classify SQL offline using a closed target profile; never connects. Usages: query, setup, benchmark."
    },
    {
      "name": "query",
      "description": "Run a bounded SQL query."
    },
    {
      "name": "measure",
      "description": "Measure a query."
    },
    {
      "name": "compare",
      "description": "Compare baseline and candidate queries."
    },
    {
      "name": "schema",
      "description": "Inspect database schema."
    },
    {
      "name": "ping",
      "description": "Check a database connection."
    },
    {
      "name": "counts",
      "description": "Inspect table row counts."
    },
    {
      "name": "space",
      "description": "Inspect database storage."
    },
    {
      "name": "watch",
      "description": "Poll a bounded query passing the static visible-effects text check."
    },
    {
      "name": "snapshot",
      "description": "Capture or compare a named query result."
    },
    {
      "name": "qstop",
      "description": "Rank SQL Server Query Store consumers."
    },
    {
      "name": "indexes",
      "description": "Inspect SQL Server missing-index evidence."
    },
    {
      "name": "plan",
      "description": "Distill a saved execution plan offline."
    },
    {
      "name": "gain",
      "description": "Report saved-output estimates."
    },
    {
      "name": "artifact",
      "description": "Read safe sections of a saved benchmark artifact offline."
    }
  ],
  "engines": [
    {
      "name": "sqlserver",
      "supportsQstop": true,
      "supportsIndexes": true,
      "parameterTypes": [
        "nvarchar",
        "nvarchar(max)",
        "varchar",
        "varchar(max)",
        "char",
        "nchar",
        "int",
        "bigint",
        "smallint",
        "tinyint",
        "bit",
        "decimal",
        "decimal(p,s)",
        "numeric",
        "numeric(p,s)",
        "float",
        "real",
        "money",
        "smallmoney",
        "date",
        "time",
        "datetime",
        "datetime2",
        "smalldatetime",
        "datetimeoffset",
        "uniqueidentifier",
        "varbinary",
        "varbinary(max)",
        "hierarchyid",
        "geography",
        "geometry"
      ]
    },
    {
      "name": "postgres",
      "supportsQstop": false,
      "supportsIndexes": false,
      "parameterTypes": [
        "nvarchar",
        "nvarchar(max)",
        "varchar",
        "varchar(max)",
        "char",
        "nchar",
        "int",
        "bigint",
        "smallint",
        "tinyint",
        "bit",
        "decimal",
        "decimal(p,s)",
        "numeric",
        "numeric(p,s)",
        "float",
        "real",
        "date",
        "time",
        "datetime",
        "datetime2",
        "datetimeoffset",
        "uniqueidentifier",
        "varbinary",
        "varbinary(max)"
      ]
    }
  ],
  "limits": {
    "queryTimeoutSeconds": {
      "min": 1,
      "max": 300
    },
    "queryMaxRows": {
      "min": 0,
      "max": 500
    },
    "comparisonRowCapPerRun": {
      "min": 0,
      "max": 1000000
    },
    "comparisonUniqueFingerprintBudgetPerCell": {
      "min": 0,
      "max": 2000000
    },
    "repeat": {
      "min": 1,
      "max": 100
    },
    "qstopTop": {
      "min": 1,
      "max": 500
    },
    "qstopWindowMinutes": {
      "min": 1,
      "max": 44640
    },
    "indexesTop": {
      "min": 1,
      "max": 500
    },
    "agentOutputBytes": {
      "min": 4096,
      "max": 1048576,
      "defaultValue": 16384
    },
    "agentCellChars": {
      "min": 0,
      "max": 4096,
      "defaultValue": 512
    },
    "sessionTempStatements": {
      "sqlserver": [
        "DECLARE scalar variables with analyzed initializers",
        "Plain SET @v = expr to a scalar local declared earlier in the same batch: allowed in query and measured SQL, denied in --setup. The RHS gets the external, stateful and cross-database checks. Compound, cursor, member and session/transaction option SET stay denied",
        "DECLARE @t TABLE (...) then INSERT/UPDATE/DELETE/MERGE/SELECT against it, declared earlier in the same batch. A table variable from --setup is not visible to measured SQL (separate batch): carry data in #temp. OUTPUT INTO @t only when the primary target is also proven local (persistent primary target: MutationNotAllowed). Aliased DML targets and user-defined table types stay denied",
        "TRUNCATE TABLE #temp (unambiguous local temp only)",
        "ALTER TABLE #temp ADD/DROP COLUMN and local CHECK/DEFAULT/NULL/UNIQUE constraints"
      ],
      "postgres": [
        "EXPLAIN over a safe SELECT (plan-only, read-only)",
        "EXPLAIN ANALYZE with full inner-statement effect analysis",
        "SELECT INTO TEMP TABLE with unambiguous single-part name",
        "TRUNCATE [ONLY] of proven current-session temps only, named unqualified or as pg_temp.name; not ON COMMIT DROP. Persistent, mixed, CASCADE, RESTART IDENTITY and other schema-qualified targets stay denied",
        "Session-temp proof for temp DML, DROP TABLE, CREATE INDEX and TRUNCATE targets is name-based. An unqualified name assumes the default search_path (pg_temp first) and is not checked against the server; when search_path may differ, write to pg_temp.name, which does not depend on it. A name must be quoted or all-ASCII-unquoted and at most 63 UTF-8 bytes where declared and where used, else never proven (quote or shorten). DROP TABLE of a name unknown offline revokes every proof. See AGENTS.md"
      ]
    },
    "artifactRead": {
      "sections": [
        "summary",
        "metrics",
        "operators"
      ],
      "manifestVersion": 1,
      "maxReportBytes": 16777216
    },
    "watchNdjson": {
      "events": [
        "started",
        "changed",
        "completed",
        "failed"
      ],
      "schemaVersion": 1,
      "sequence": "strictly increasing from started; exactly one terminal record",
      "history": "no retention: every change is emitted immediately"
    }
  },
  "outputModes": [
    "text",
    "json",
    "json-summary",
    "agent"
  ],
  "safetyAnalysis": {
    "analysisKind": "static-visible-effects",
    "analysisContractVersion": 1,
    "hiddenEffectsVerified": false,
    "objectAndPermissionStatus": "unknown"
  }
}
```

### Historical context

This is not that measurement. The audit from 2026-09-28 in `plans/2026-09-28-project-audit.md` talks about the help under the path from `Get-Command` and about the absence of `capabilities`, `validate` and `mcp`. The audit spec from 2026-09-26 in `docs/superpowers/specs/2026-09-26-project-audit.md` talks about a shorter list than the then-current HEAD, including the absence of `qstop` and `indexes`. The current fact is the transcript above, at HEAD `ece0d7fcaef90325d20865db639c12b47bc2de4e`, not at `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.

In this measurement the divergence is on the help surface. The installed name set is a subset of the repository help, not a disjoint set. The provenance of the file, its hash and the date are not in this step.

### UNPROVEN

- The text of `dotnet run --project src/SqlHarness.Cli -- --version`. Condition: run that command and record stdout. The repository help advertises `-v, --version`. That is not this text. That command was not run.
- Whether the installed binary accepts `capabilities`, `validate`, `mcp`, `doctor`, `artifact`, `qstop` or `indexes`. The absence of a name in `--help` is not a rejection code. Condition: run each of these names on the path from `Get-Command` and record the code and both streams. They were not run.
- Whether `sqlharness -v` prints the same `1.0.0` as `--version`. Condition: run `sqlharness -v` on that path. Only `--version` was run.
- Whether the `mcp` help in the repository build lists `serve`. Condition: `dotnet run --project src/SqlHarness.Cli -- mcp --help`. On the main help page there is `mcp` and no `serve`.
- The file hash, the file times and the PE version resource apart from the `Version` property from `Get-Command`. Condition: read the metadata of that file without replacing it. `1.0.0.0` is only that property.
- Whether an empty `dotnet run` stderr means there was no rebuild. Condition: an MSBuild log from the same command. It was not in the captured streams.

## 015/T2

The read is from 2026-10-02T19:41:48.1799751+02:00 on this Windows host. The divergence from `## 015/T1` did not disappear. There is no RESOLVED EXTERNALLY marking. There was no reinstallation. Nothing was published, copied, moved or used to replace the binary. No user configuration was written. No `targets.json`, profiles, passwords, connection strings or environment variable values were read. Neither `dotnet test`, `dotnet run`, `--help` nor `--version` was run. The file was not run. This step does not record an update procedure.

### Get-Command

`Get-Command sqlharness -All` returns one object. There was no exception. `CommandType` = `Application`. `Name` = `sqlharness.exe`. `Source`, `Path` and `Definition` = `C:\Users\rgone\.local\bin\sqlharness.exe`. The command object's `Version` = `1.0.0.0`. There was no second application, function or alias. The path and that `Version` property are the same as in `## 015/T1`.

That path is the only binary of this step. The attributes are `Archive`. `LinkType` is null, `Target` is null. In this read the file is not a link. The hash, the times and the version resource concern that file.

### File

| Field | Value |
|---|---|
| Path | `C:\Users\rgone\.local\bin\sqlharness.exe` |
| Length | 95604247 B |
| SHA-256 | `bb702504d05acc124f0c50030818d61ec4790bf33eb3b35dfe0b651983be5c2f` |
| Created | 2026-07-19T22:30:24.3089416+02:00 |
| Last write | 2026-09-23T10:58:00.4821692+02:00 |

`Get-FileHash -Algorithm SHA256` returned `BB702504D05ACC124F0C50030818D61EC4790BF33EB3B35DFE0B651983BE5C2F`. A separate SHA-256 digest of the stream of those same bytes gave the same digits. In the table they are lowercase, like the stdout hashes in `## 015/T1`. The stream length equals the file length, 95604247 B. After the read, the length and the last write time were the same.

The observed time zone is Windows `Central European Standard Time`. It has daylight saving time. The daylight name is `Central European Daylight Time`. The zone's base offset is `+01:00`. The Windows zone string shows that base offset. At both timestamps and at the moment of the read daylight saving time was in force, so their offset is `+02:00`, not `+01:00`. The `Kind` of the local creation and last-write times is `Local`. The same creation moment in UTC is 2026-07-19T20:30:24.3089416Z. The same last-write moment in UTC is 2026-09-23T08:58:00.4821692Z.

### Version resource

A `FileVersionInfo` read without running the file. Four fields are present and non-empty. None is missing.

| Field | Value |
|---|---|
| FileVersion | `1.0.0.0` |
| ProductVersion | `1.0.0+3d2c41892d15d563c22dba9c917c71aa31cb66ff` |
| OriginalFilename | `sqlharness.dll` |
| FileDescription | `sqlharness` |

`FileVersion` equals the `Version` property from `Get-Command`. It is not the `--version` stdout text from `## 015/T1`. That stdout is the characters `1.0.0`. `ProductVersion` is a different, longer string. It does not equal the `buildId` from `capabilities --json` in `## 015/T1` (`1.0.0+ece0d7fcaef90325d20865db639c12b47bc2de4e`). The suffix after `+` is not the SHA-256 of that file.

Those 40 hex characters are the full id of an object in this repository. `git cat-file -t` returned `commit`. `git merge-base --is-ancestor` relative to HEAD `d2a07291ffc6b51f40792635c681d9fc47f7552e` exited with code 0, so that commit is an ancestor, not that HEAD. The committer date, `git log -1 --format=%cI`, is 2026-09-11T11:28:10+02:00. The subject is `Merge branch 'feat/postgres-engine'`. The file creation time is earlier than that committer date. The file last-write time is later. Those are three times, not a publication log.

`OriginalFilename` ends with `.dll`. The file from `Get-Command` ends with `.exe`. That is a resource value. It is not a publication method.

### Authenticode

`Get-AuthenticodeSignature`: `Status` = `NotSigned`, `SignatureType` = `None`, `IsOSBinary` = false. The signing certificate is null. There is no signature, so there is no signer name. The absence of a signature is not a GitHub release and is not a local `dotnet publish`.

### Provenance

The provenance apart from the path, the length, the SHA-256, the two file times, the four version resource fields and the absence of a signature is UNPROVEN. The condition that would establish it: a release checksum or a publication log equal to the SHA-256 `bb702504d05acc124f0c50030818d61ec4790bf33eb3b35dfe0b651983be5c2f`. This step has no such match. No source was invented.

Before this entry, the worktree text contained neither that SHA-256 nor the full id `3d2c41892d15d563c22dba9c917c71aa31cb66ff`. The git object exists separately from that text. This is not a catalog review outside that worktree and is not a release checksum.

### Command list

The list of available commands is the list from the installed help, taken in `## 015/T1` under `Names in the help`. The order is the order from that table, not a sorting. The help was not run again. This is not a new execution of each name.

- `query`
- `measure`
- `compare`
- `gain`
- `plan`
- `schema`
- `ping`
- `counts`
- `space`
- `watch`
- `snapshot`

The repository page is in `## 015/T1`, in the same table. That table: only in the installed help no name; only in the repository help `artifact`, `qstop`, `indexes`, `capabilities`, `doctor`, `validate`, `mcp`. `capabilities`, `validate` and `mcp` are not on the list above. The divergence remains.

The `mcp` difference between the repository help and `capabilities --json` remains in `## 015/T1`. This step does not change it. The condition from `## 015/T1`, whether the absence of a name in the help is a rejection code, also remains there. These names were not run.

### UNPROVEN

- The provenance of the file apart from the fields recorded above. Condition: a release checksum or a publication log equal to the SHA-256 `bb702504d05acc124f0c50030818d61ec4790bf33eb3b35dfe0b651983be5c2f`. The absence of a signature, `OriginalFilename` = `sqlharness.dll` and the `ProductVersion` suffix do not satisfy that condition. That match is not present in this step.
- Whether the bytes hashed in this step are the bytes of the process that printed the help and `--version` in `## 015/T1`. Condition: the file hash taken at that measurement. T1 recorded the path and the stdout hash, not the file hash. In this step the file was not replaced. This proves neither a replacement of the bytes nor their identity.
- Whether commit `3d2c41892d15d563c22dba9c917c71aa31cb66ff` produced those bytes. The condition is the same as for the provenance: a file hash match. The string in the version resource and being an ancestor of HEAD do not do that.

## 015/T3

The record is from 2026-10-02 on this Windows host. The divergence from `## 015/T1` and `## 015/T2` remains. There is no RESOLVED EXTERNALLY marking. This step records the update procedure and does not run it. No release archive or `SHA256SUMS` file was downloaded. `C:\Users\rgone\.local\bin\sqlharness.exe` was not copied, moved or replaced. The copy `C:\Users\rgone\.local\bin\sqlharness.exe.bak-bb702504d05a` was not created. The staging directory was not created. No user configuration was written. No `targets.json`, profiles, passwords, connection strings or environment variable values were read. `dotnet test` was not run. No product code was changed. This procedure is not a local `dotnet publish` and is not a copy of a publish result into `.local\bin`. This plan does not authorize such a publish.

### Decisions

- The divergence remains. The installed help, recorded in `## 015/T1`, has `query`, `measure`, `compare`, `gain`, `plan`, `schema`, `ping`, `counts`, `space`, `watch`, `snapshot`. It has no `capabilities`, `validate` nor `mcp`. The repository help from that measurement also has `artifact`, `qstop`, `indexes`, `capabilities`, `doctor`, `validate` and `mcp`.
- RESOLVED EXTERNALLY was not marked. `## 015/T2` did not mark it either.
- The procedure is recorded below. No installation was performed. Writing the plan does not replace the PATH file.
- The tag choice stays `v0.3.1`. It does not close the divergence. Step 5 is expected to reject an asset built from that tag. Closing the divergence requires a build newer than every published tag in the table. This plan does not authorize such a build. The git read is at the tag choice.
- The provenance of the file stays UNPROVEN, on the condition from `## 015/T2`. The release metadata query did not yield a checksum equal to the SHA-256 `bb702504d05acc124f0c50030818d61ec4790bf33eb3b35dfe0b651983be5c2f`.
- Both `--version` strings are `1.0.0`, one from the PATH binary and one from the `version` field in `capabilities --json`. That string does not show the divergence. The divergence is in the help name set.
- The `mcp` difference is recorded in `## 015/T1`: the repository help lists `mcp`, while `capabilities --json` from the same measurement has that name neither in `commands` nor as a substring. It stays as recorded. It is out of scope for this step. No product code was changed.

### Boundaries

No live DB. No profile and no secret. Only this Windows host. No other platform. No product code change.

### Scope of future files

None in the repository. The copy path `C:\Users\rgone\.local\bin\sqlharness.exe.bak-bb702504d05a` lies outside the repository and was not created. The staging directory `C:\Users\rgone\AppData\Local\Temp\sqlharness-015-staging` also lies outside the repository and was not created.

### Release query

The read was read-only. The names, publication dates and the draft and prerelease flags were retrieved. No asset bytes were downloaded. No token was printed. The query did not fail.

`gh release list --repo rgonek/sqlharness --limit 20` returned four items and marked `v0.3.1` as Latest. Then `gh release view` for `v0.3.1`, `v0.3.0`, `v0.2.0` and `v0.1.0`, with the tag, publication date, draft, prerelease and asset name fields. For `v0.3.1` the size and content type were also read; only the presence flags of the two contract files enter this table. The zip size was not compared with the PATH file length.

| Tag | Publication | Draft | Prerelease | `sqlharness-win-x64.zip` | `SHA256SUMS` |
|---|---|---|---|---|---|
| `v0.3.1` | 2026-08-04T13:30:57Z | no | no | yes | yes |
| `v0.3.0` | 2026-08-03T18:39:26Z | no | no | yes | yes |
| `v0.2.0` | 2026-08-03T07:41:50Z | no | no | yes | yes |
| `v0.1.0` | 2026-07-18T15:12:22Z | no | no | yes | yes |

For each of those four tags the asset list also has `sqlharness-linux-x64.tar.gz` and `sqlharness-osx-arm64.tar.gz`. This procedure does not use them. That is not evidence from another platform.

The tag list is not `--help` text. Step 5's stdout was not recorded. The expectation from git is at the tag choice: step 5 is to reject an asset built from `v0.3.1`. No tag in the table closes the divergence if its asset is built from that tag.

The last write time of the PATH file from `## 015/T2` is 2026-09-23T08:58:00.4821692Z. The newest publication in the table is earlier. That is not a publication log and does not identify the file with a tag. The provenance stays UNPROVEN.

The distribution contract is in the README, in the `Install a release binary` section: the `sqlharness-win-x64.zip` archive, the `SHA256SUMS` file and verification before unpacking. The example in that section inserts `v0.1.0` with a comment that the tag must be replaced with the chosen one. That example is not the choice of this diagnosis. The procedure below selects `v0.3.1`, because this read points to it as the newest published tag that has both contract files. That choice does not prove the help.

Every tag in the table is earlier than the commits that added `capabilities`, `validate` and `mcp`. That is a git read from this worktree. There was no new release query and no publication was invented. The publication date of `v0.3.1` from the table, `2026-08-04T13:30:57Z`, is not that tag's committer date.

`git rev-parse v0.3.1` returns the tag object `aec21ca362ae1c1b04f769e5b3a25ea1769b77a9`. `git cat-file -t v0.3.1` returned `tag`. `git rev-parse "v0.3.1^{commit}"` returns the commit `1f5707897b57f24c69380c542928407fc97693a2`. The committer date, `git log -1 --format=%cI v0.3.1`, is `2026-08-04T15:28:51+02:00`. The subject is `fix: include SqlClient Azure authentication`.

`c798c1a1334b5a6ff14f94c9a65e1bdfa79748ea` added `capabilities` and `validate`. The committer date is `2026-09-26T19:18:08+02:00`. The subject is `feat: add offline capabilities and validation commands`. `git merge-base --is-ancestor c798c1a1334b5a6ff14f94c9a65e1bdfa79748ea v0.3.1` exited with code 1, so that commit is not an ancestor of `v0.3.1`. `git merge-base --is-ancestor v0.3.1 c798c1a1334b5a6ff14f94c9a65e1bdfa79748ea` exited with code 0, so `v0.3.1` is an ancestor of that commit.

`f2d988aa88a0d120facbde3b3dc8ef380a199512` added `mcp`. The committer date is `2026-09-28T14:35:34+02:00`. The subject is `feat: host a profile-scoped MCP server over stdio`. `git merge-base --is-ancestor f2d988aa88a0d120facbde3b3dc8ef380a199512 v0.3.1` exited with code 1. `git merge-base --is-ancestor v0.3.1 f2d988aa88a0d120facbde3b3dc8ef380a199512` exited with code 0.

`v0.3.0` points at the commit `f607e5f5d68aa1d6d946fe28d9a3df4a65b23f1b`, committer `2026-08-03T20:23:29+02:00`, subject `docs: define complete select syntax contract`. `v0.2.0` points at the commit `af71450ebac48c72cd579ac848b007f424e39b68`, committer `2026-07-30T12:30:29+02:00`, subject `fix: polish watch/snapshot gain success and stop edges`. `v0.1.0` points at the commit `14c58c0f19761f977910a55ad4ac168d8a4dd9a3`, committer `2026-07-18T17:06:55+02:00`, subject `Merge branch 'feature/sqlharness-extraction' — release job repository context`. For `v0.3.0`, `v0.2.0` and `v0.1.0` both `git merge-base --is-ancestor` runs, from `c798c1a1334b5a6ff14f94c9a65e1bdfa79748ea` and from `f2d988aa88a0d120facbde3b3dc8ef380a199512`, exited with code 1, and both in the other direction with code 0.

Step 5 is expected to reject an asset built from `v0.3.1`. That step was not run and the archive was not downloaded. Closing the divergence requires a build newer than every published tag in the table. This plan does not authorize such a build.

### Paths

The README example unpacks to `$HOME\bin\sqlharness` and appends that directory to `PATH`. `Get-Command sqlharness -All` in `## 015/T1` and `## 015/T2` returned `C:\Users\rgone\.local\bin\sqlharness.exe`. These are different paths. Merely replacing the target from the README example would leave that PATH binary in place. This procedure does not unpack to `$HOME\bin\sqlharness` and does not append that directory to `PATH`. The replacement target in step 6, if a later task gets to it, is the file from `Get-Command`, not the directory from the example.

The staging directory is `C:\Users\rgone\AppData\Local\Temp\sqlharness-015-staging`. It is not the worktree `D:\Dev\sqlharness\.worktrees\plan-015-installation-alignment` and it is not the PATH binary's directory `C:\Users\rgone\.local\bin`.

### Procedure

The commands below are for a later task. This step did not run them. This record does not replace the PATH file. The blocks run in order. The first exception stops the rest. Steps 3–5 can stop already after step 2: the PATH file is then still the original, and the copy whose SHA-256 is `bb702504d05acc124f0c50030818d61ec4790bf33eb3b35dfe0b651983be5c2f` is already in place. This procedure does not delete that matching copy. If the hash of the copy from step 2 is not that SHA-256, step 2 deletes that bad copy and only then throws. The bad copy is not left behind, so the next orderly run does not stop at the prohibition on overwriting an existing name.

The procedure's tag is `v0.3.1`. Do not swap it for `v0.1.0` from the README example just because the example is written that way. That tag, like every tag in the table, is earlier than the `capabilities`, `validate` and `mcp` commits. Step 5 is expected to reject an asset built from `v0.3.1`. The git detail is at the tag choice.

1. Stop, unless `Get-Command sqlharness -All` still returns exactly one object and that object is the application `C:\Users\rgone\.local\bin\sqlharness.exe`, and the SHA-256 of that file is still `bb702504d05acc124f0c50030818d61ec4790bf33eb3b35dfe0b651983be5c2f`.

```powershell
$path = "C:\Users\rgone\.local\bin\sqlharness.exe"
$expectedInstalled = "bb702504d05acc124f0c50030818d61ec4790bf33eb3b35dfe0b651983be5c2f"
$cmd = @(Get-Command sqlharness -All)
if ($cmd.Count -ne 1) {
  throw "Stop: Get-Command sqlharness -All does not return exactly one object."
}
$one = $cmd[0]
if ([string]$one.CommandType -cne "Application" -or $one.Name -cne "sqlharness.exe" -or $one.Path -cne $path -or $one.Source -cne $path -or $one.Definition -cne $path) {
  throw "Stop: the result is not exactly $path."
}
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
if ($hash -cne $expectedInstalled) {
  throw "Stop: the SHA-256 of the PATH file is not $expectedInstalled. Do not touch the file."
}
```

2. Copy that file to `C:\Users\rgone\.local\bin\sqlharness.exe.bak-bb702504d05a`. If that name already exists, stop. Do not overwrite it. After copying, compare the copy's SHA-256 with `bb702504d05acc124f0c50030818d61ec4790bf33eb3b35dfe0b651983be5c2f`. On a mismatch, delete that bad copy and stop. Do not replace the PATH file. The deletion concerns only the copy with the wrong hash. Do not delete a matching copy.

```powershell
$path = "C:\Users\rgone\.local\bin\sqlharness.exe"
$expectedInstalled = "bb702504d05acc124f0c50030818d61ec4790bf33eb3b35dfe0b651983be5c2f"
$backup = "C:\Users\rgone\.local\bin\sqlharness.exe.bak-bb702504d05a"
if (Test-Path -LiteralPath $backup) {
  throw "Stop: the copy already exists. Do not overwrite it."
}
Copy-Item -LiteralPath $path -Destination $backup
$backupHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $backup).Hash.ToLowerInvariant()
if ($backupHash -cne $expectedInstalled) {
  Remove-Item -LiteralPath $backup
  if (Test-Path -LiteralPath $backup) {
    throw "Stop: the copy does not have SHA-256 $expectedInstalled and could not be removed. Do not replace the PATH file. Do not overwrite this copy."
  }
  throw "Stop: the copy did not have SHA-256 $expectedInstalled. The bad copy was removed. Do not replace the PATH file."
}
```

3. Download `sqlharness-win-x64.zip` and `SHA256SUMS` of the `v0.3.1` tag into the staging directory. Verify the zip with the README comparison against `SHA256SUMS`. On a mismatch, stop and leave the PATH file untouched. This step does not unpack the archive.

The README comparison, section `Install a release binary`, is those two lines, run in the staging directory: the first takes the first non-empty token of the `SHA256SUMS` line that contains `sqlharness-win-x64.zip`; the second compares it, after `ToLowerInvariant`, with the `Get-FileHash` of that zip using the SHA256 algorithm and on a mismatch throws `Checksum mismatch.` That exception also leaves the PATH file untouched. The copy from step 2 stays.

```powershell
$version = "v0.3.1"
$staging = "C:\Users\rgone\AppData\Local\Temp\sqlharness-015-staging"
$worktree = "D:\Dev\sqlharness\.worktrees\plan-015-installation-alignment"
$pathDir = "C:\Users\rgone\.local\bin"
$stagingFull = [System.IO.Path]::GetFullPath($staging)
$worktreeFull = [System.IO.Path]::GetFullPath($worktree)
$pathDirFull = [System.IO.Path]::GetFullPath($pathDir)
$stagingPrefix = $stagingFull.TrimEnd('\') + '\'
$worktreePrefix = $worktreeFull.TrimEnd('\') + '\'
$pathPrefix = $pathDirFull.TrimEnd('\') + '\'
if ($stagingPrefix.StartsWith($worktreePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
  throw "Stop: staging lies in the worktree."
}
if ($stagingPrefix.StartsWith($pathPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
  throw "Stop: staging lies in the PATH binary directory."
}
if (Test-Path -LiteralPath $staging) {
  throw "Stop: the staging directory already exists. Do not mix the download with existing content."
}
New-Item -ItemType Directory -Path $staging | Out-Null
$base = "https://github.com/rgonek/sqlharness/releases/download/$version"
Invoke-WebRequest "$base/sqlharness-win-x64.zip" -OutFile (Join-Path $staging "sqlharness-win-x64.zip")
Invoke-WebRequest "$base/SHA256SUMS" -OutFile (Join-Path $staging "SHA256SUMS")
Push-Location -LiteralPath $staging
try {
  $sumHits = @(Select-String 'sqlharness-win-x64.zip' SHA256SUMS)
  if ($sumHits.Count -ne 1) {
    throw "Stop: SHA256SUMS does not point to exactly one sqlharness-win-x64.zip line. The PATH file stays untouched."
  }
  $expected = (Select-String 'sqlharness-win-x64.zip' SHA256SUMS).Line.Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries)[0]
  if ((Get-FileHash sqlharness-win-x64.zip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected.ToLowerInvariant()) { throw "Checksum mismatch." }
}
finally {
  Pop-Location
}
```

4. Unpack into a directory inside the staging area, not onto the PATH file. Record the SHA-256 of the extracted `sqlharness.exe`. That document has no expected hash for that file. Do not compare it with `bb702504d05acc124f0c50030818d61ec4790bf33eb3b35dfe0b651983be5c2f` as if that were the target hash. A different value is not an error of this step. The same value is also not an error of this step and is not proof that the PATH file already is that release.

The zip layout is not in this document. If `sqlharness.exe` does not lie directly in the extraction directory, stop. Do not look for another file. The PATH file stays.

```powershell
$path = "C:\Users\rgone\.local\bin\sqlharness.exe"
$pathDir = "C:\Users\rgone\.local\bin"
$staging = "C:\Users\rgone\AppData\Local\Temp\sqlharness-015-staging"
$extract = Join-Path $staging "extract"
if (-not $extract.StartsWith($staging + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
  throw "Stop: the unpacked output does not lie in staging."
}
if ($extract -eq $pathDir -or $extract -eq $path) {
  throw "Stop: rozpakowanie wskazuje plik PATH albo jego katalog."
}
if (Test-Path -LiteralPath $extract) {
  throw "Stop: the unpack directory already exists."
}
Expand-Archive -LiteralPath (Join-Path $staging "sqlharness-win-x64.zip") -DestinationPath $extract
$stagedExe = Join-Path $extract "sqlharness.exe"
if (-not (Test-Path -LiteralPath $stagedExe)) {
  throw "Stop: sqlharness.exe does not lie directly in the unpack directory. Do not look for another file. The PATH file stays."
}
$stagedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $stagedExe).Hash.ToLowerInvariant()
Write-Output $stagedHash
```

Record the printed SHA-256 in the note of the task that performs the installation. Do not write it back into this document as the expected value.

5. Run `--help` on the staged file, not through the `sqlharness` name from PATH. Continue only if that help lists `capabilities`, `validate` and `mcp`. A command name is the first token of a line under `COMMANDS:` whose indentation is exactly four spaces, as in `## 015/T1`. The token comparison is case-sensitive. As long as that run has not happened, whether the GitHub release closes this divergence is UNPROVEN. The tag list does not prove the help text.

```powershell
$stagedExe = "C:\Users\rgone\AppData\Local\Temp\sqlharness-015-staging\extract\sqlharness.exe"
$helpText = & $stagedExe --help
if ($LASTEXITCODE -ne 0 -or $null -eq $helpText) {
  throw "Stop: --help of the file from staging did not exit with code 0 or produced no stdout. The PATH file stays."
}
$names = New-Object System.Collections.Generic.List[string]
$inCommands = $false
foreach ($line in @($helpText -split '\r?\n')) {
  if ($line -eq "COMMANDS:") {
    $inCommands = $true
    continue
  }
  if (-not $inCommands) { continue }
  $four = $line.Length -ge 4 -and $line.Substring(0, 4) -eq "    "
  $deeper = $line.Length -ge 5 -and $line.Substring(0, 5) -eq "     "
  if ($four -and -not $deeper) {
    $token = ($line.Trim() -split '\s+', 2)[0]
    if (-not [string]::IsNullOrEmpty($token)) { $names.Add($token) }
  }
}
foreach ($required in @("capabilities", "validate", "mcp")) {
  if (-not $names.Contains($required)) {
    throw "Stop: the help of the file from staging does not list $required. The PATH file stays. The copy from step 2 stays."
  }
}
```

6. Only then replace `C:\Users\rgone\.local\bin\sqlharness.exe` with the staged file. Before `-Force`, both the SHA-256 of the copy from step 2 and the SHA-256 of the current PATH file must be `bb702504d05acc124f0c50030818d61ec4790bf33eb3b35dfe0b651983be5c2f`. Otherwise stop and do not call `-Force`. The copy from step 2 stays. Do not delete it. Do not append `$HOME\bin\sqlharness` to `PATH`. This plan does not perform step 6.

Compare the hash after the replacement with the hash of the staged file, that is with the file you are copying right now. Do not compare it with `bb702504d05acc124f0c50030818d61ec4790bf33eb3b35dfe0b651983be5c2f` as if that were the target hash. If the hash after the replacement is not the hash of the staged file, copy the backup from step 2 back onto the PATH file, then compare the PATH file's SHA-256 with that same known hash and stop. Do not delete the copy from step 2. If the copy reports an error, stop.

```powershell
$path = "C:\Users\rgone\.local\bin\sqlharness.exe"
$backup = "C:\Users\rgone\.local\bin\sqlharness.exe.bak-bb702504d05a"
$stagedExe = "C:\Users\rgone\AppData\Local\Temp\sqlharness-015-staging\extract\sqlharness.exe"
$expectedInstalled = "bb702504d05acc124f0c50030818d61ec4790bf33eb3b35dfe0b651983be5c2f"
if (-not (Test-Path -LiteralPath $backup)) {
  throw "Stop: the copy from step 2 is missing. Do not replace the PATH file."
}
if (-not (Test-Path -LiteralPath $stagedExe)) {
  throw "Stop: the file from staging is missing. Do not replace the PATH file."
}
$backupHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $backup).Hash.ToLowerInvariant()
$pathHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
if ($backupHash -cne $expectedInstalled -or $pathHash -cne $expectedInstalled) {
  throw "Stop: the SHA-256 of the copy or of the current PATH file is not $expectedInstalled. Do not call -Force. Do not replace the PATH file."
}
$stagedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $stagedExe).Hash.ToLowerInvariant()
Copy-Item -LiteralPath $stagedExe -Destination $path -Force
$replaced = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
if ($replaced -cne $stagedHash) {
  Copy-Item -LiteralPath $backup -Destination $path -Force
  $restored = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
  if ($restored -cne $expectedInstalled) {
    throw "Stop: the hash after the swap is not the hash of the file from staging, and restoring the copy did not bring back SHA-256 $expectedInstalled. Do not delete the copy from step 2."
  }
  throw "Stop: the hash of the PATH file after the copy is not the hash of the file from staging. The copy from step 2 was copied back onto the PATH file. Do not delete this copy."
}
```

### Test table

One row for each command that this diagnosis actually ran. The commands from the procedure above are not rows, because this step did not run them.

| Command | Outcome | Where recorded |
|---|---|---|
| `Get-Command sqlharness -All` | one `Application` object; `Source`, `Path` and `Definition` = `C:\Users\rgone\.local\bin\sqlharness.exe`; T2 the same | `## 015/T1`, `## 015/T2` |
| `sqlharness --help` | code 0; 376 B; names without `capabilities`, `validate`, `mcp` | `## 015/T1` |
| `sqlharness --version` | code 0; text `1.0.0` | `## 015/T1` |
| `dotnet run --project src/SqlHarness.Cli -- --help` | code 0; 1971 B; has `capabilities`, `validate`, `mcp` | `## 015/T1` |
| `dotnet run --project src/SqlHarness.Cli -- capabilities --json` | code 0; 6956 B; `mcp` does not occur in this JSON | `## 015/T1` |
| `Get-FileHash -Algorithm SHA256` of the PATH file | `bb702504d05acc124f0c50030818d61ec4790bf33eb3b35dfe0b651983be5c2f`; length 95604247 B | `## 015/T2` |
| `FileVersionInfo.GetVersionInfo` | `FileVersion` `1.0.0.0`; `ProductVersion` `1.0.0+3d2c41892d15d563c22dba9c917c71aa31cb66ff`; `OriginalFilename` `sqlharness.dll`; `FileDescription` `sqlharness` | `## 015/T2` |
| `Get-AuthenticodeSignature` | `Status` `NotSigned`; signing certificate null | `## 015/T2` |
| `gh release list --repo rgonek/sqlharness --limit 20` and `gh release view` of four tags | code 0; four published tags; each with `sqlharness-win-x64.zip` and `SHA256SUMS` | this section, `Release query` |
| `git rev-parse`, `git cat-file -t`, `git log -1 --format=%cI` and `git merge-base --is-ancestor` for the tags in the table and for `c798c1a1334b5a6ff14f94c9a65e1bdfa79748ea` and `f2d988aa88a0d120facbde3b3dc8ef380a199512` | codes at the tag choice; both feature commits are not ancestors of any of the four tags (code 1); each tag is an ancestor of both (code 0) | at the tag choice |
| `git diff --check` | exit 0; no output | this row for the `## 015/T3` entry; the T1 and T2 runs are also exit 0, in the task reports outside git |
| `git diff --check` for the guard entry | exit 0; no output | this row |
| `dotnet test` | not run; the plan forbids application tests when only writing documents | this row |

### UNPROVEN

- The `--help` text of the release, that is step 5's stdout. Condition: perform step 5 and record stdout. That run has not happened, so that text is not here. The expectation from git, recorded at the tag choice, is not that stdout: step 5 is to reject an asset built from `v0.3.1`. Closing the divergence requires a build newer than every published tag in the table. This plan does not authorize such a build.
- Whether the hash of any release asset equals a build of this worktree. Condition: hash the extracted `sqlharness.exe` and the build from this worktree, then compare those two hashes. This document has no such pair. The PATH file's SHA-256 is not the expected hash of the extracted file and is not that pair.
- The `SHA256SUMS` body and the zip bytes. Condition: download those files in step 3 and record the checksum and the comparison result. That query took the asset names, dates and flags. It did not read the checksums.
- The provenance of the PATH file apart from the fields from `## 015/T2`. The condition stays the one from `## 015/T2`: a release checksum or a publication log equal to the SHA-256 `bb702504d05acc124f0c50030818d61ec4790bf33eb3b35dfe0b651983be5c2f`. That query does not satisfy this condition.
- Any platform other than this Windows host. Condition: repeat the diagnosis on that platform. That was not done. The linux and osx archive names are not that evidence.
- Live DB. Condition: a measurement on a live database. That was not done. This diagnosis does not require it and does not replace it.

### No live evidence and no evidence from another platform

This step did not connect to a database. There is no live evidence. The diagnosis was not run on Linux, macOS or another host. The names `sqlharness-linux-x64.tar.gz` and `sqlharness-osx-arm64.tar.gz` from the release metadata are not evidence from another platform, because those archives were not downloaded and were not run. The absence of live evidence and the absence of evidence from another platform is a gap, not a positive result.
