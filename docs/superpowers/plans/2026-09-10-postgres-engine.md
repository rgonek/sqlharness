# PostgreSQL Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a Postgres dialect pack behind the existing `sqlharness` CLI so every current command works against a closed `engine: postgres` profile without changing SQL Server behavior.

**Architecture:** Engine is a property of the resolved target. A small `ISqlDialect` plus an `EngineSessionFactory` dispatch to Npgsql, a SqlParserCS safety classifier, EXPLAIN-based benchmarks, and `pg_catalog` helpers. SQL Server code stays in place and is wrapped, not rewritten.

**Tech Stack:** .NET 8, C#, Spectre.Console.Cli, Microsoft.Data.SqlClient (unchanged), Npgsql 8.0.8, SqlParserCS 0.6.5, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-10-postgres-engine-design.md`

## Global Constraints

- One `sqlharness` binary. Engine lives on the locked target, not as a per-command mode.
- Omitted profile `engine` means `sqlserver`. Existing `targets.json` must keep loading.
- Postgres v1 auth is `sql` only (`sqlUser` + `passwordEnvVar`). `ad-default` / `azure-cli` / `integrated` on Postgres → exit 2. Do not echo secrets or unknown engine values.
- `trustServerCertificate: true` → Npgsql `SslMode=Disable`; `false` → `SslMode=Require`. No `sslMode` field.
- `--engine` is valid only with `--unsafe-direct`. Mixing `--engine` with a profile → exit 2.
- Safety: fail-closed parse; session `TEMP` without mutation flags; persistent DML only with `--allow-mutation --confirm-database`; persistent DDL always denied; no `#temp` translation.
- Measure/compare on Postgres: single EXPLAIN-able statement; `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` is the timed run; result equivalence uses an unmeasured sidecar.
- `CpuTimeMs` is `0` on Postgres. `logicalReads` are shared/local hit+read blocks. `missingIndexes` is empty.
- Catalog helpers remain fixed internal SQL. Postgres `LIKE` and unquoted identifiers are case-sensitive.
- Runtime floor PostgreSQL 14+; playground image `postgres:16` on host port 5433.
- `dotnet test` without integration env vars opens no database connections.
- Exit codes unchanged (`0/2/3/4/5/6/7/8`).
- Do not weaken SQL Server tests or behavior.

## File structure

| File | Responsibility |
| --- | --- |
| `src/SqlHarness.Core/SqlEngine.cs` | `SqlEngine` enum and `SqlEngineNames` parse/format |
| `src/SqlHarness.Core/Dialect/ISqlDialect.cs` | Dialect pack surface |
| `src/SqlHarness.Core/Dialect/SqlDialects.cs` | `For(SqlEngine)` registry |
| `src/SqlHarness.Core/Dialect/SqlServerDialect.cs` | Wraps existing T-SQL classifier, ping, catalog, STATISTICS, showplan |
| `src/SqlHarness.Core/Postgres/PostgresDialect.cs` | Postgres pack |
| `src/SqlHarness.Core/Postgres/PostgresConnectionString.cs` | Host/port/SSL/password builder |
| `src/SqlHarness.Core/Postgres/NpgsqlSessionFactory.cs` | Connect, identity, notices → `Messages` |
| `src/SqlHarness.Core/Postgres/PostgresSafetyClassifier.cs` | SqlParserCS fail-closed classifier |
| `src/SqlHarness.Core/Postgres/PostgresParameters.cs` | Reject SQL Server-only types; bind to Npgsql |
| `src/SqlHarness.Core/Postgres/PostgresPing.cs` | Fixed ping/identity SQL |
| `src/SqlHarness.Core/Postgres/PostgresBenchmark.cs` | EXPLAIN wrap, stats parse, sidecar |
| `src/SqlHarness.Core/Postgres/PostgresPlanDistiller.cs` | EXPLAIN JSON → `DistilledPlan` |
| `src/SqlHarness.Core/Postgres/PostgresCounts.cs` | Catalog + exact count SQL/readers |
| `src/SqlHarness.Core/Postgres/PostgresSchema.cs` | Catalog SQL/reader (same report records) |
| `src/SqlHarness.Core/Postgres/PostgresSpace.cs` | Storage SQL/reader (same report records) |
| `src/SqlHarness.Core/EngineSessionFactory.cs` | Dispatch `ISqlSessionFactory` by `ResolvedTarget.Engine` |
| `src/SqlHarness.Core/Targets/TargetProfile.cs` | Optional `Engine` |
| `src/SqlHarness.Core/Targets/TargetResolver.cs` | Resolve engine; reject illegal Postgres auth |
| `src/SqlHarness.Core/Contracts.cs` | `SqlTargetRequest.Engine` |
| `src/SqlHarness.Core/SqlHarnessModule.cs` | Classify/bind/catalog/benchmark via dialect |
| `src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs` | `--engine` on `TargetSettings` |
| `scripts/setup-local-postgres.ps1` | Pagila playground |
| `tests/SqlHarness.Tests/Postgres/` | New unit tests |
| `tests/SqlHarness.Tests/Fixtures/*.explain.json` | Distiller fixtures |
| `tests/SqlHarness.Tests/Integration/PostgresIntegrationFactAttribute.cs` | Opt-in PG integration |

Final `ISqlDialect` (grown across tasks; do not stub unused methods — add a member in the task that first needs it):

```csharp
internal interface ISqlDialect
{
    SqlEngine Engine { get; }
    string IdentitySql { get; }
    string PingSql { get; }
    SqlSafetyDecision Classify(
        string sql,
        SqlUsage usage,
        string? database,
        bool allowMutation,
        string? confirmDatabase,
        IReadOnlySet<string> sessionTempTables);
    IReadOnlySet<string> CollectSessionTempTables(string sql);
    IReadOnlyList<SqlHarnessParameter> ParseParameters(IReadOnlyList<string> inputs);
    void ValidateMeasuredBatch(string sql);
    Task<CollectedCompareRun> ExecuteBenchmarkRunAsync(
        ISqlSession session,
        string sql,
        IReadOnlyList<SqlHarnessParameter> parameters,
        int timeoutSeconds,
        int repetition,
        string variant,
        CanonicalResultAccumulator raw,
        bool captureComparison,
        int comparisonMaximumRows,
        CancellationToken ct);
    DistilledPlan DistillPlan(string document);
    string CountsCatalogSql { get; }
    string SchemaSql { get; }
    string SpaceSql { get; }
}
```

`CollectedCompareRun` is the existing private nested record on `SqlHarnessModule`, moved to `internal` in Task 5 so both dialects can return it. Keep the current property names.

---

### Task 1: Engine on profiles, resolve, and `--engine`

**Files:**
- Create: `src/SqlHarness.Core/SqlEngine.cs`
- Modify: `src/SqlHarness.Core/Targets/TargetProfile.cs`
- Modify: `src/SqlHarness.Core/Targets/TargetResolver.cs`
- Modify: `src/SqlHarness.Core/Contracts.cs` (`SqlTargetRequest`)
- Modify: `src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs`
- Test: `tests/SqlHarness.Tests/Targets/ProfileStoreTests.cs`
- Test: `tests/SqlHarness.Tests/Targets/TargetResolverTests.cs`
- Test: `tests/SqlHarness.Tests/Cli/CommandTests.cs`
- Test: `tests/SqlHarness.Tests/SqlEngineTests.cs`

**Interfaces:**
- Consumes: existing `TargetProfile` JSON load (`UnmappedMemberHandling.Disallow`), `TargetResolver.Resolve`, `TargetSettings.TryTarget`.
- Produces: `public enum SqlEngine { SqlServer = 0, Postgres = 1 }`; `internal static class SqlEngineNames` with `Parse(string? value)` and `Format(SqlEngine engine)` using `"sqlserver"` / `"postgres"`; `TargetProfile.Engine` (`string?`, default null); `SqlTargetRequest.Engine` (`string?`, default null); `ResolvedTarget.Engine` (`SqlEngine`, default `SqlServer`).

- [ ] **Step 1: Write failing engine parse and profile tests**

```csharp
public sealed class SqlEngineTests
{
    [Theory]
    [InlineData(null, SqlEngine.SqlServer)]
    [InlineData("", SqlEngine.SqlServer)]
    [InlineData("sqlserver", SqlEngine.SqlServer)]
    [InlineData("SQLServer", SqlEngine.SqlServer)]
    [InlineData("postgres", SqlEngine.Postgres)]
    [InlineData("Postgres", SqlEngine.Postgres)]
    public void Parse_accepts_omitted_sqlserver_and_postgres(string? value, SqlEngine expected) =>
        Assert.Equal(expected, SqlEngineNames.Parse(value));

    [Fact]
    public void Parse_rejects_unknown_without_echoing_value()
    {
        const string secret = "engine-secret-never-emit";
        var error = Assert.Throws<SqlHarnessSafetyException>(() => SqlEngineNames.Parse(secret));
        Assert.Contains("Unknown engine", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, error.Message);
    }
}
```

Add to `ProfileStoreTests`:

```csharp
[Fact]
public void Loads_postgres_engine_and_defaults_omitted_engine_to_null()
{
    var path = WriteTemp("""
        {
          "pg": {
            "engine": "postgres",
            "server": "localhost,5432",
            "database": "appdb",
            "vars": {},
            "auth": "sql",
            "sqlUser": "sqlharness",
            "passwordEnvVar": "SQLHARNESS_PG_PASSWORD",
            "trustServerCertificate": true
          },
          "mssql": {
            "server": "s",
            "database": "d",
            "vars": {},
            "auth": "integrated"
          }
        }
        """);
    try
    {
        var profiles = ProfileStore.Load(path);
        Assert.Equal("postgres", profiles["pg"].Engine);
        Assert.Null(profiles["mssql"].Engine);
    }
    finally { File.Delete(path); }
}
```

Add to `TargetResolverTests`:

```csharp
[Fact]
public void Profile_without_engine_resolves_sqlserver()
{
    var target = TargetResolver.Resolve(
        new SqlTargetRequest("prod-eu", new Dictionary<string, string>
        {
            ["tenant"] = "acme",
            ["env"] = "uat",
        }),
        Profiles);
    Assert.Equal(SqlEngine.SqlServer, target.Engine);
}

[Fact]
public void Postgres_sql_profile_resolves_postgres_engine()
{
    var profiles = new Dictionary<string, TargetProfile>
    {
        ["local-pg"] = new(
            "localhost,5432",
            "appdb",
            new Dictionary<string, string>(),
            "sql",
            SqlUser: "sqlharness",
            PasswordEnvVar: "SQLHARNESS_PG_PASSWORD",
            TrustServerCertificate: true,
            Engine: "postgres"),
    };
    var target = TargetResolver.Resolve(new SqlTargetRequest("local-pg", new Dictionary<string, string>()), profiles);
    Assert.Equal(SqlEngine.Postgres, target.Engine);
    Assert.Equal(AuthStrategy.Sql, target.Auth.Strategy);
}

[Theory]
[InlineData("ad-default")]
[InlineData("azure-cli")]
[InlineData("integrated")]
public void Postgres_rejects_non_sql_auth(string auth)
{
    var profiles = new Dictionary<string, TargetProfile>
    {
        ["pg"] = new("localhost,5432", "appdb", new Dictionary<string, string>(), auth, Engine: "postgres"),
    };
    var error = Assert.Throws<SqlHarnessSafetyException>(
        () => TargetResolver.Resolve(new SqlTargetRequest("pg", new Dictionary<string, string>()), profiles));
    Assert.Contains("sql", error.Message, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public void Direct_postgres_requires_engine_postgres()
{
    var target = TargetResolver.Resolve(
        new SqlTargetRequest(
            null, new Dictionary<string, string>(),
            Server: "localhost,5432", Database: "appdb", Auth: "sql",
            UnsafeDirect: true, SqlUser: "u", PasswordEnvVar: "P",
            TrustServerCertificate: true, Engine: "postgres"),
        new Dictionary<string, TargetProfile>());
    Assert.Equal(SqlEngine.Postgres, target.Engine);
    Assert.Equal("direct", target.Mode);
}
```

CLI (`CommandTests`): `--engine postgres` with a profile name fails before dispatch; `--unsafe-direct --engine postgres` sets `SqlTargetRequest.Engine`.

```csharp
[Fact]
public async Task Query_rejects_engine_combined_with_profile()
{
    var exit = await SqlHarnessCli.Create(new FakeModule(), new StringWriter())
        .RunAsync(["query", "dev", "--engine", "postgres", "--file", sqlFile]);
    Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
}

[Fact]
public async Task Query_unsafe_direct_carries_engine()
{
    var exit = await app.RunAsync([
        "query", "--unsafe-direct", "--engine", "postgres",
        "--server", "localhost,5432", "--database", "appdb",
        "--auth", "sql", "--sql-user", "u", "--password-env-var", "P",
        "--file", sqlFile]);
    Assert.Equal(0, exit);
    Assert.Equal("postgres", Assert.IsType<SqlHarnessQueryOperation>(...).Target.Engine);
}
```

Use the existing `FakeModule` / temp `--file` helpers already in `CommandTests.cs`.

- [ ] **Step 2: Run the new tests and confirm they fail**

Run: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~SqlEngineTests|FullyQualifiedName~Loads_postgres_engine|FullyQualifiedName~Postgres_sql_profile|FullyQualifiedName~Query_rejects_engine"`

Expected: FAIL (missing types / missing `Engine` properties).

- [ ] **Step 3: Implement engine parse, profile field, resolver, CLI**

`SqlEngine.cs`:

```csharp
namespace SqlHarness.Core;

public enum SqlEngine
{
    SqlServer = 0,
    Postgres = 1,
}

internal static class SqlEngineNames
{
    internal const string SqlServer = "sqlserver";
    internal const string Postgres = "postgres";

    internal static SqlEngine Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, SqlServer, StringComparison.OrdinalIgnoreCase))
            return SqlEngine.SqlServer;
        if (string.Equals(value, Postgres, StringComparison.OrdinalIgnoreCase))
            return SqlEngine.Postgres;
        throw new SqlHarnessSafetyException("Unknown engine.");
    }

    internal static string Format(SqlEngine engine) => engine switch
    {
        SqlEngine.SqlServer => SqlServer,
        SqlEngine.Postgres => Postgres,
        _ => throw new SqlHarnessSafetyException("Unknown engine."),
    };
}
```

Add `string? Engine = null` as the last parameter of `TargetProfile` and of `SqlTargetRequest`.

Add `SqlEngine Engine = SqlEngine.SqlServer` as the last parameter of `ResolvedTarget`.

`ProfileStore.Load`: after the existing completeness checks, if `profile.Engine` is non-null/non-white, call `SqlEngineNames.Parse(profile.Engine)` so unknown values fail at load with the existing wrap (`Could not parse target profiles file`).

`TargetResolver.ResolveProfile`: `var engine = SqlEngineNames.Parse(profile.Engine);` then if `engine == SqlEngine.Postgres && auth.Strategy != AuthStrategy.Sql` throw `SqlHarnessSafetyException("Postgres targets require sql authentication.")`. Return `new ResolvedTarget(..., engine)`.

`TargetResolver.ResolveDirect`: parse `request.Engine`; same Postgres auth guard; pass engine into `ResolvedTarget`.

`TargetSettings`: add `[CommandOption("--engine <NAME>")] public string? Engine { get; set; }`. Treat a non-empty `Engine` as a direct option in `directValues` so profile+`--engine` fails with the existing “Direct SQL options are valid only with `--unsafe-direct`.” message. Pass `Engine` into `new SqlTargetRequest(...)`.

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~SqlEngineTests|FullyQualifiedName~ProfileStoreTests|FullyQualifiedName~TargetResolverTests|FullyQualifiedName~CommandTests"`

Expected: PASS. Full `dotnet test` must stay green.

- [ ] **Step 5: Commit**

```powershell
git add src/SqlHarness.Core/SqlEngine.cs src/SqlHarness.Core/Targets/TargetProfile.cs src/SqlHarness.Core/Targets/TargetResolver.cs src/SqlHarness.Core/Contracts.cs src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs tests/SqlHarness.Tests/SqlEngineTests.cs tests/SqlHarness.Tests/Targets/ProfileStoreTests.cs tests/SqlHarness.Tests/Targets/TargetResolverTests.cs tests/SqlHarness.Tests/Cli/CommandTests.cs
git commit -m "feat: resolve postgres engine on profiles and --unsafe-direct"
```

---

### Task 2: Npgsql session, identity, ping

**Files:**
- Modify: `Directory.Packages.props` (add `Npgsql` 8.0.8)
- Modify: `src/SqlHarness.Core/SqlHarness.Core.csproj` (PackageReference `Npgsql`)
- Create: `src/SqlHarness.Core/Postgres/PostgresConnectionString.cs`
- Create: `src/SqlHarness.Core/Postgres/PostgresPing.cs`
- Create: `src/SqlHarness.Core/Postgres/NpgsqlSessionFactory.cs`
- Create: `src/SqlHarness.Core/EngineSessionFactory.cs`
- Create: `src/SqlHarness.Core/Dialect/ISqlDialect.cs` (Engine, IdentitySql, PingSql only)
- Create: `src/SqlHarness.Core/Dialect/SqlDialects.cs`
- Create: `src/SqlHarness.Core/Dialect/SqlServerDialect.cs`
- Create: `src/SqlHarness.Core/Postgres/PostgresDialect.cs`
- Modify: `src/SqlHarness.Core/SqlExecution.cs` (share loopback match; Map Npgsql later in module)
- Modify: `src/SqlHarness.Core/SqlHarnessModule.cs` (default factory + ping SQL from dialect)
- Modify: `src/SqlHarness.Core/SqlHarnessModule.cs` `MapException` for `Npgsql.NpgsqlException`
- Test: `tests/SqlHarness.Tests/Postgres/PostgresConnectionStringTests.cs`
- Test: `tests/SqlHarness.Tests/PingTests.cs` (add postgres ping SQL selection test)

**Interfaces:**
- Consumes: `ResolvedTarget.Engine`, `AuthSpec` password env var, `ISqlSession` / `ISqlSessionFactory`, `SqlClientSessionFactory.IsLoopbackEndpoint` / `TargetMatches` (move match helpers to `SqlExecution` so both factories call them).
- Produces: `PostgresConnectionString.Build(ResolvedTarget target, int connectTimeoutSeconds)` → string; `PostgresPing.Sql` and `PostgresPing.IdentitySql`; `NpgsqlSessionFactory : ISqlSessionFactory`; `EngineSessionFactory`; `ISqlDialect.PingSql` / `IdentitySql`; module production ctor uses `EngineSessionFactory`.

- [ ] **Step 1: Write failing connection-string and ping-dispatch tests**

```csharp
public sealed class PostgresConnectionStringTests
{
    [Fact]
    public void Builds_disable_ssl_for_trust_server_certificate()
    {
        Environment.SetEnvironmentVariable("SQLHARNESS_PG_PASSWORD", "secret");
        try
        {
            var target = new ResolvedTarget(
                "localhost,5432", "appdb",
                AuthSpec.Parse("sql", "sqlharness", "SQLHARNESS_PG_PASSWORD", true),
                "profile", SqlEngine.Postgres);
            var cs = PostgresConnectionString.Build(target, 15);
            Assert.Contains("Host=localhost", cs, StringComparison.Ordinal);
            Assert.Contains("Port=5432", cs, StringComparison.Ordinal);
            Assert.Contains("Database=appdb", cs, StringComparison.Ordinal);
            Assert.Contains("Username=sqlharness", cs, StringComparison.Ordinal);
            Assert.Contains("SSL Mode=Disable", cs, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", cs.Replace("Password=secret", "Password=***", StringComparison.Ordinal));
        }
        finally { Environment.SetEnvironmentVariable("SQLHARNESS_PG_PASSWORD", null); }
    }

    [Fact]
    public void Default_port_is_5432_and_require_ssl_without_trust()
    {
        Environment.SetEnvironmentVariable("P", "x");
        try
        {
            var target = new ResolvedTarget(
                "db.example.com", "appdb",
                AuthSpec.Parse("sql", "u", "P", false),
                "direct", SqlEngine.Postgres);
            var cs = PostgresConnectionString.Build(target, 15);
            Assert.Contains("Host=db.example.com", cs, StringComparison.Ordinal);
            Assert.Contains("Port=5432", cs, StringComparison.Ordinal);
            Assert.Contains("SSL Mode=Require", cs, StringComparison.OrdinalIgnoreCase);
        }
        finally { Environment.SetEnvironmentVariable("P", null); }
    }

    [Fact]
    public void Missing_password_env_fails_without_echoing_name_value_pair()
    {
        Environment.SetEnvironmentVariable("MISSING_PG_PASSWORD", null);
        var target = new ResolvedTarget(
            "localhost,5432", "appdb",
            AuthSpec.Parse("sql", "u", "MISSING_PG_PASSWORD", true),
            "profile", SqlEngine.Postgres);
        var error = Assert.Throws<SqlHarnessSafetyException>(() => PostgresConnectionString.Build(target, 15));
        Assert.Contains("MISSING_PG_PASSWORD", error.Message, StringComparison.Ordinal);
    }
}
```

Ping dispatch (add to `PingTests.cs`): a postgres profile must execute `PostgresPing.Sql`, not `PingQuery.Sql`. Reuse `FakeSession`; add a `Profiles()` variant with `Engine: "postgres"` and `auth: sql`. Assert `Assert.Equal(PostgresPing.Sql, Assert.Single(session.Commands).Sql)`. Existing ping tests must still assert `PingQuery.Sql`.

- [ ] **Step 2: Run tests, confirm fail**

Run: `dotnet test --filter "FullyQualifiedName~PostgresConnectionStringTests|FullyQualifiedName~PingTests"`

Expected: FAIL (missing types / ping still uses `PingQuery.Sql`).

- [ ] **Step 3: Implement connection string, session factory, dialect ping, module dispatch**

`PostgresPing.cs` constants exactly:

```sql
SELECT CAST(1 AS int) AS ok,
       current_database() AS db,
       COALESCE(inet_server_addr()::text, 'localhost') AS server,
       current_user AS login;
```

Identity:

```sql
SELECT current_database() AS DatabaseName,
       COALESCE(inet_server_addr()::text, 'localhost') AS ServerName;
```

`PostgresConnectionString.Build`: parse `host[,port]` from `target.Server` (single comma); default port 5432; read password from `target.Auth.PasswordEnvVar`; use `NpgsqlConnectionStringBuilder` (`Host`, `Port`, `Database`, `Username`, `Password`, `Timeout`, `SslMode` Disable vs Require). Throw the same missing-password safety exception as `AuthSpec`.

Move `TargetMatches`, `IsLoopbackEndpoint`, `NormalizeServer` (SQL Server Azure suffix stays SQL Server-only; Postgres factory calls a `TargetMatches(expected, server, database)` that still skips server check on loopback and uses ordinal-ignore-case host compare otherwise). Postgres identity does not strip `.database.windows.net`.

`NpgsqlSessionFactory.ConnectAsync`: build CS, open `NpgsqlConnection`, subscribe `Notice` → session `Messages`, run identity SQL, `TargetMatches`, set `Identity` with `Engine: "postgres"` only for Postgres (leave `Engine` null on SQL Server identity so existing JSON stays stable). Map connect failures: missing password → safety; auth failures → keep as thrown for module mapping.

`EngineSessionFactory`: switch on `target.Engine` to `_sqlServer` / `_postgres`.

Production `SqlHarnessModule()` ctor: `new EngineSessionFactory(new SqlClientSessionFactory(new AzureCli()), new NpgsqlSessionFactory())`.

`ExecutePingAsync`: after resolve, `var sql = SqlDialects.For(target.Engine).PingSql;` then execute that.

`MapException`: add `Npgsql.NpgsqlException when phase == Authentication => Authentication` and `Npgsql.NpgsqlException => SqlExecution`.

`ISqlDialect` for this task only has `Engine`, `IdentitySql`, `PingSql`. `SqlDialects.For` returns `SqlServerDialect` or `PostgresDialect`.

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj`

Expected: PASS. No live Postgres connection.

- [ ] **Step 5: Commit**

```powershell
git add Directory.Packages.props src/SqlHarness.Core/SqlHarness.Core.csproj src/SqlHarness.Core/Postgres src/SqlHarness.Core/Dialect src/SqlHarness.Core/EngineSessionFactory.cs src/SqlHarness.Core/SqlExecution.cs src/SqlHarness.Core/SqlHarnessModule.cs tests/SqlHarness.Tests/Postgres tests/SqlHarness.Tests/PingTests.cs
git commit -m "feat: connect Npgsql sessions and ping postgres targets"
```

---

### Task 3: Postgres safety classifier and query-pipeline wiring

**Files:**
- Modify: `Directory.Packages.props` + `SqlHarness.Core.csproj` (SqlParserCS 0.6.5)
- Create: `src/SqlHarness.Core/Postgres/PostgresSafetyClassifier.cs`
- Modify: `src/SqlHarness.Core/SqlSafety.cs` (`SqlSafetyDecision.SessionTempTables`; optional populate on SQL Server)
- Modify: `src/SqlHarness.Core/Dialect/ISqlDialect.cs` (add `Classify`, `CollectSessionTempTables`)
- Modify: `SqlServerDialect` / `PostgresDialect`
- Modify: `src/SqlHarness.Core/SqlHarnessModule.cs` (query, watch, snapshot, measure, compare: dialect.Classify; pass setup temp names into query classification)
- Test: `tests/SqlHarness.Tests/Postgres/PostgresSafetyTests.cs`

**Interfaces:**
- Consumes: `SqlUsage`, `SqlSafetyDecision`, `SqlSafetyReason`, SqlParserCS `Parser.ParseSql(sql, new PostgreSqlDialect())` (if the installed API is `SqlQueryParser`, use that — the tests below are SQL in / decision out).
- Produces: `PostgresSafetyClassifier.Classify(...)` with the spec contract; `ISqlDialect.Classify` / `CollectSessionTempTables`.

- [ ] **Step 1: Write failing classifier tests**

```csharp
public sealed class PostgresSafetyTests
{
    private readonly PostgresSafetyClassifier _classifier = new();

    [Fact]
    public void Select_with_cte_is_read_only()
    {
        var decision = _classifier.Classify(
            "WITH x AS (SELECT 1 AS n) SELECT n FROM x",
            SqlUsage.Query, "appdb", false, null, Empty);
        Assert.True(decision.Allowed);
        Assert.False(decision.HasMutation);
    }

    [Fact]
    public void Create_temp_and_insert_are_session_local()
    {
        var decision = _classifier.Classify("""
            CREATE TEMP TABLE t (id int);
            INSERT INTO t VALUES (1);
            SELECT * FROM t;
            """, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.True(decision.Allowed);
        Assert.True(decision.HasSessionLocalWork);
        Assert.False(decision.HasMutation);
        Assert.Contains("t", decision.SessionTempTables);
    }

    [Fact]
    public void Insert_into_setup_temp_is_session_local()
    {
        var setup = _classifier.Classify(
            "CREATE TEMP TABLE t (id int)",
            SqlUsage.CompareSetup, "appdb", false, null, Empty);
        var query = _classifier.Classify(
            "INSERT INTO t SELECT 1; SELECT * FROM t",
            SqlUsage.Query, "appdb", false, null, setup.SessionTempTables);
        Assert.True(query.Allowed);
        Assert.False(query.HasMutation);
    }

    [Fact]
    public void Persistent_insert_requires_mutation_flags()
    {
        var denied = _classifier.Classify(
            "INSERT INTO public.items SELECT 1",
            SqlUsage.Query, "appdb", false, null, Empty);
        Assert.False(denied.Allowed);
        Assert.Equal(SqlSafetyReason.MutationNotAllowed, denied.Reason);

        var allowed = _classifier.Classify(
            "INSERT INTO public.items SELECT 1",
            SqlUsage.Query, "appdb", true, "appdb", Empty);
        Assert.True(allowed.Allowed);
        Assert.True(allowed.HasMutation);

        var mismatch = _classifier.Classify(
            "INSERT INTO public.items SELECT 1",
            SqlUsage.Query, "appdb", true, "otherdb", Empty);
        Assert.False(mismatch.Allowed);
        Assert.Equal(SqlSafetyReason.DatabaseConfirmationMismatch, mismatch.Reason);
    }

    [Fact]
    public void Select_into_is_rejected()
    {
        var decision = _classifier.Classify(
            "SELECT 1 INTO persistent_copy",
            SqlUsage.Query, "appdb", true, "appdb", Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.SelectIntoNotAllowed, decision.Reason);
    }

    [Fact]
    public void Persistent_create_table_is_unsupported()
    {
        var decision = _classifier.Classify(
            "CREATE TABLE t (id int)",
            SqlUsage.Query, "appdb", true, "appdb", Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, decision.Reason);
    }

    [Theory]
    [InlineData("BEGIN")]
    [InlineData("COMMIT")]
    [InlineData("SET search_path TO public")]
    [InlineData("COPY t FROM STDIN")]
    [InlineData("DO $$ BEGIN NULL; END $$")]
    [InlineData("PREPARE x AS SELECT 1")]
    [InlineData("SELECT nextval('s')")]
    [InlineData("SELECT dblink('dbname=other','select 1')")]
    public void Denied_constructs(string sql)
    {
        var decision = _classifier.Classify(sql, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.False(decision.Allowed);
    }

    [Fact]
    public void Parse_error_is_denied_without_sql_echo()
    {
        const string sql = "SELECTnotvalid secret-token";
        var decision = _classifier.Classify(sql, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.Equal(SqlSafetyReason.ParseError, decision.Reason);
        Assert.DoesNotContain("secret-token", decision.RejectionDescription);
    }

    [Fact]
    public void Cross_schema_select_is_allowed()
    {
        var decision = _classifier.Classify(
            "SELECT * FROM other.contracts",
            SqlUsage.Query, "appdb", false, null, Empty);
        Assert.True(decision.Allowed);
    }

    private static readonly IReadOnlySet<string> Empty = new HashSet<string>(StringComparer.Ordinal);
}
```

Also add a module-level test in `QueryTests.cs` (or a small `PostgresQueryTests.cs`): postgres profile, `CREATE TEMP TABLE` batch, FakeSession, assert success and that SQL Server classifier is not used (command SQL is the user SQL). Existing T-SQL `#temp` tests must still pass on sqlserver profiles.

- [ ] **Step 2: Run tests, confirm fail**

Run: `dotnet test --filter "FullyQualifiedName~PostgresSafetyTests"`

Expected: FAIL (missing classifier).

- [ ] **Step 3: Implement classifier and wire module**

Parse with SqlParserCS PostgreSQL dialect. Empty statement list → `UnsupportedStatement`.

Top-level allow:
- `SELECT` (including `WITH`); if the query has a writable CTE or `SELECT INTO`, classify the write target.
- `INSERT` / `UPDATE` / `DELETE` / `MERGE`
- `CREATE TABLE` only when TEMP/TEMPORARY
- `CREATE INDEX` / `DROP TABLE` (and `DROP INDEX`) only when the table is session-local
- `CREATE TABLE AS SELECT` only when TEMP

Session-local when: created in this batch as TEMP; name in `sessionTempTables`; or first identifier is `pg_temp` / starts with `pg_temp_`. Fold unquoted identifiers to lowercase; keep quoted case.

`SELECT INTO` (Postgres) → `SelectIntoNotAllowed` even with mutation flags.

Persistent DML → mutation path (same confirm-database ordinal match as SQL Server). Persistent DDL → `UnsupportedStatement`.

Always deny (statement type or function name in AST): transaction control, `PREPARE`/`EXECUTE`/`DEALLOCATE`, `DO`, `CALL`, `COPY`, `SET`/`RESET`, `LISTEN`/`NOTIFY`, `LOAD`, `GRANT`/`REVOKE`, `VACUUM`, `nextval`/`setval`, `pg_sleep`, `dblink*`, `pg_read_file`, `pg_ls_dir`, `lo_import`.

`CollectSessionTempTables` returns names from a successful classify (use `decision.SessionTempTables`).

Module: after `TargetResolver.Resolve`, `var dialect = SqlDialects.For(target.Engine);`. Replace `new SqlSafetyClassifier()` with `dialect.Classify`. For measure/compare: classify setup first with empty temp set; classify measured SQL with `setupDecision.SessionTempTables`.

SQL Server dialect delegates to existing `SqlSafetyClassifier` (add an overload or optional `sessionTempTables` ignored because `#` is syntactic).

Rejection messages must not include SQL text.

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add Directory.Packages.props src/SqlHarness.Core src/SqlHarness.Core/Postgres/PostgresSafetyClassifier.cs tests/SqlHarness.Tests/Postgres/PostgresSafetyTests.cs
git commit -m "feat: classify postgres SQL with session TEMP tracking"
```

---

### Task 4: Parameters and Npgsql binding

**Files:**
- Create: `src/SqlHarness.Core/Postgres/PostgresParameters.cs`
- Modify: `Postgres/NpgsqlSessionFactory.cs` (bind `SqlHarnessParameter` onto `NpgsqlCommand`)
- Modify: `ISqlDialect.ParseParameters` — SQL Server keeps `SqlParameterParser.Parse`; Postgres calls Parse then `PostgresParameters.Validate`
- Modify: module query/watch/snapshot/measure/compare to use `dialect.ParseParameters`
- Test: `tests/SqlHarness.Tests/Postgres/PostgresParameterTests.cs`

**Interfaces:**
- Consumes: `SqlParameterParser.Parse`, `SqlHarnessParameter`, `SqlParameterReferenceValidator`.
- Produces: `PostgresParameters.Validate(IReadOnlyList<SqlHarnessParameter>)` throwing `SqlHarnessSafetyException` for `money`, `smallmoney`, `smalldatetime`, `hierarchyid`, `geography`, `geometry`; `tinyint` values outside 0–255; `NpgsqlSession.ExecuteReaderAsync` mapping (see table in spec).

- [ ] **Step 1: Write failing parameter tests**

```csharp
[Theory]
[InlineData("amount:money=1.23")]
[InlineData("when:smalldatetime=2026-07-29T12:00:00")]
[InlineData("path:hierarchyid=/1/2/")]
[InlineData("loc:geography=4326;POINT(0 0)")]
public void Rejects_sql_server_only_types(string input)
{
    var parsed = SqlParameterParser.Parse([input]);
    var error = Assert.Throws<SqlHarnessSafetyException>(() => PostgresParameters.Validate(parsed));
    Assert.DoesNotContain("1.23", error.Message);
}

[Fact]
public void Accepts_mapped_types()
{
    var parsed = SqlParameterParser.Parse([
        "n:int=1",
        "u:uniqueidentifier=0f8fad5b-d9cb-469f-a165-70867728950e",
        "t:nvarchar=witaj",
        "d:datetime2=2026-07-29T12:00:00",
        "flag:bit=true",
        "tiny:tinyint=255",
        "bin:varbinary=AQID",
    ]);
    PostgresParameters.Validate(parsed);
}

[Fact]
public void Tinyint_256_is_rejected()
{
    var parsed = SqlParameterParser.Parse(["tiny:tinyint=256"]);
    Assert.Throws<SqlHarnessSafetyException>(() => PostgresParameters.Validate(parsed));
}
```

Add a FakeSession query test: postgres profile, `--param id:int=1`, assert the execution command still carries the parsed parameter list (module uses dialect.ParseParameters). SQL Server query parameter tests remain green.

- [ ] **Step 2: Run tests, confirm fail**

Run: `dotnet test --filter "FullyQualifiedName~PostgresParameterTests"`

Expected: FAIL.

- [ ] **Step 3: Implement validate + bind**

`PostgresParameters.Validate`: switch on `SqlDbType` / `UdtTypeName`; reject listed types; for `TinyInt` require byte 0–255 (parser may already constrain — still guard).

Binding in `NpgsqlSession` (`ExecuteReaderAsync`):

| SqlDbType | NpgsqlDbType / CLR |
| --- | --- |
| NVarChar, VarChar, Char, NChar | Text / Varchar / Char |
| Int, BigInt, SmallInt | Integer / Bigint / Smallint |
| TinyInt | Smallint, value as short |
| Bit | Boolean |
| Decimal, Money (rejected before bind) | Numeric |
| Float, Real | Double / Real |
| Date, Time, DateTime, DateTime2 | Date / Time / Timestamp |
| DateTimeOffset | TimestampTz |
| UniqueIdentifier | Uuid |
| VarBinary | Bytea |
| DBNull | `DBNull.Value` with the mapped type |

`Size = -1` → Npgsql `-1`. Parameter names: keep `@name` in SQL; Npgsql accepts `$name` or `@name` depending on version — bind as `command.Parameters.Add(new NpgsqlParameter(parameter.Name, ...)` matching the SQL `@` placeholders already required by `SqlParameterReferenceValidator`.

`SqlServerDialect.ParseParameters` → `SqlParameterParser.Parse`. `PostgresDialect.ParseParameters` → parse then validate.

Module: `dialect.ParseParameters(operation.Parameters)` everywhere `SqlParameterParser.Parse` is used for user SQL.

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/SqlHarness.Core/Postgres/PostgresParameters.cs src/SqlHarness.Core/Postgres/NpgsqlSessionFactory.cs src/SqlHarness.Core/Dialect src/SqlHarness.Core/SqlHarnessModule.cs tests/SqlHarness.Tests/Postgres/PostgresParameterTests.cs
git commit -m "feat: bind and validate postgres parameters"
```

---

### Task 5: Measure and compare via EXPLAIN

**Files:**
- Create: `src/SqlHarness.Core/Postgres/PostgresBenchmark.cs`
- Modify: `ISqlDialect` (`ValidateMeasuredBatch`, `ExecuteBenchmarkRunAsync`)
- Modify: `SqlHarnessModule.ExecuteBenchmarkRunAsync` — move current body into `SqlServerDialect`; module calls `dialect.ExecuteBenchmarkRunAsync`
- Make `CollectedCompareRun` / needed artifact types `internal` (not private nested) so the dialect can return them — keep the existing record shape
- Modify: `src/SqlHarness.Core/Artifacts.cs` — if plan document starts with `{` or `[`, write `*.explain.json` instead of `*.sqlplan`; distilled file stays `*.plan.json`
- Test: `tests/SqlHarness.Tests/Postgres/PostgresBenchmarkTests.cs`
- Modify: existing `MeasureTests` / `CompareTests` still pass on sqlserver fakes

**Interfaces:**
- Consumes: `ISqlSession.ExecuteReaderAsync`, EXPLAIN JSON document as a single text column.
- Produces: wrap `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)\n` + stripped trailing semicolon; `ElapsedTimeMs = (Planning Time + Execution Time)` rounded to long ms; `CpuTimeMs = 0`; `LogicalReads` = sum of shared/local hit+read on all nodes; `Tables` keyed by `Relation Name`; sidecar when `captureComparison` is true; multi-statement measured SQL → `SqlHarnessSafetyException` (exit 2).

- [ ] **Step 1: Write failing wrap/parse and module tests**

```csharp
[Fact]
public void Wrap_prefixes_explain_and_strips_one_trailing_semicolon()
{
    Assert.Equal(
        "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)\nSELECT 1",
        PostgresBenchmark.Wrap("SELECT 1;"));
}

[Fact]
public void Parse_reads_timing_buffers_and_tables()
{
    var stats = PostgresBenchmark.ParseStats(ExplainFixture);
    Assert.Equal(0, stats.CpuTimeMs);
    Assert.Equal(15, stats.ElapsedTimeMs); // 10.4 + 4.6 → 15
    Assert.Equal(3, stats.LogicalReads);
    Assert.Equal(3, stats.Tables["foo"]);
}

[Fact]
public void Validate_rejects_second_statement()
{
    Assert.Throws<SqlHarnessSafetyException>(
        () => PostgresBenchmark.ValidateMeasuredBatch("SELECT 1; SELECT 2"));
}
```

`ExplainFixture` is a JSON array with one object: `Planning Time` 10.4, `Execution Time` 4.6, Plan `Node Type` Seq Scan, `Relation Name` foo, `Shared Hit Blocks` 2, `Shared Read Blocks` 1, `Local Hit Blocks` 0, `Local Read Blocks` 0.

Module test with FakeSession on a postgres profile:
1. measure repeat 1: commands include the EXPLAIN wrap, not `SET STATISTICS IO ON`; no sidecar.
2. compare with default `ordered`: first command EXPLAIN wrap, second command the bare `SELECT`; result hash comes from the sidecar reader.

FakeSession must return an EXPLAIN JSON row for the first execute and a one-column result for the sidecar. Keep this fake local to the postgres test file.

- [ ] **Step 2: Run tests, confirm fail**

Run: `dotnet test --filter "FullyQualifiedName~PostgresBenchmarkTests"`

Expected: FAIL.

- [ ] **Step 3: Implement wrap, parse, dialect benchmark, artifact extension**

`ValidateMeasuredBatch`: parse with the same Postgres parser; require exactly one statement that is a `SELECT` / `VALUES` / `WITH … SELECT` (not a writable CTE). Otherwise throw `SqlHarnessSafetyException` with a message that does not include SQL.

`ExecuteBenchmarkRunAsync` (Postgres):
1. `ValidateMeasuredBatch(sql)` (also call from module before connect for fail-closed).
2. Execute wrap; read field 0 as string JSON (EXPLAIN returns one row).
3. Parse stats + keep raw JSON as the single plan document in `PlanXmls` (name stays for compatibility; content is JSON).
4. If `captureComparison`, execute bare `sql` with the same parameters and collect fingerprints using the existing `CollectCompareAsync` path (extract that helper to `internal` if needed). Do not add sidecar duration to stats.
5. Warm-up path is the same method with `captureComparison: false`.

SQL Server dialect: current `SET STATISTICS IO/TIME/XML` body unchanged.

`ExecutionPlanParser.Parse`: if `xml.TrimStart().StartsWith('<')` keep XML path; if JSON, build `ExecutionPlan` operators from distilled Postgres nodes (`NodeId` sequential, `PhysicalOp` from `Node Type`, `Object` from `Relation Name`, warnings if node has `Warnings` / `Never Executed`). Used for variant operator summaries.

Artifact writer: `PlanFileName` uses `.explain.json` when the document is JSON, else `.sqlplan`.

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj`

Expected: PASS. Existing artifact tests still see `.sqlplan` for XML fixtures.

- [ ] **Step 5: Commit**

```powershell
git add src/SqlHarness.Core/Postgres/PostgresBenchmark.cs src/SqlHarness.Core/Artifacts.cs src/SqlHarness.Core/SqlHarnessModule.cs src/SqlHarness.Core/Diagnostics.cs src/SqlHarness.Core/Dialect tests/SqlHarness.Tests/Postgres/PostgresBenchmarkTests.cs
git commit -m "feat: measure and compare postgres with EXPLAIN ANALYZE"
```

---

### Task 6: EXPLAIN JSON plan distillation

**Files:**
- Create: `src/SqlHarness.Core/Postgres/PostgresPlanDistiller.cs`
- Create: `tests/SqlHarness.Tests/Fixtures/seq-scan.explain.json` (the Task 5 fixture, plus a Hash Join child `Plans` array)
- Modify: `src/SqlHarness.Core/SqlHarnessModule.cs` `ExecutePlan` — detect XML vs JSON
- Modify: `src/SqlHarness.Cli/Commands/PlanCommand.cs` description text
- Modify: `ISqlDialect.DistillPlan` — SQL Server calls `PlanDistiller.Distill`; Postgres calls `PostgresPlanDistiller.Distill`
- Test: `tests/SqlHarness.Tests/Postgres/PostgresPlanDistillerTests.cs`
- Test: `tests/SqlHarness.Tests/Cli/PlanCommandTests.cs` (JSON file succeeds; garbage fails with exit 2)

**Interfaces:**
- Consumes: EXPLAIN `FORMAT JSON` text (array or object).
- Produces: `DistilledPlan` mapping in the spec; `missingIndexes` empty; `plan` command auto-detect; `*.plan.json` still rejected as input if it is the distilled schema (no `Plan`/`Node Type`).

- [ ] **Step 1: Write failing distiller and CLI tests**

```csharp
[Fact]
public void Distills_seq_scan_and_hash_join()
{
    var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "seq-scan.explain.json"));
    var plan = PostgresPlanDistiller.Distill(json);
    var root = Assert.Single(plan.Statements).Root;
    Assert.Equal("Hash Join", root.PhysicalOp);
    Assert.Equal("Seq Scan", root.Children[0].PhysicalOp);
    Assert.Equal("foo", root.Children[0].ObjectName);
    Assert.True(root.CostFraction is >= 0 and <= 1);
    Assert.Empty(Assert.Single(plan.Statements).MissingIndexes);
}

[Fact]
public void Invalid_json_throws_without_echoing_payload()
{
    const string secret = "explain-secret";
    var error = Assert.Throws<SqlHarnessSafetyException>(() => PostgresPlanDistiller.Distill(secret));
    Assert.DoesNotContain(secret, error.Message);
}
```

CLI: run `plan` on the fixture with `--json`; assert `statements[0].root.physicalOp`. Run `plan` on a temp file containing `not-a-plan`; assert exit 2 and no dispatch if the module is a recording fake — `ExecutePlan` lives in the real module, so use `SqlHarnessModule` with no session (plan is offline).

Detection: trim; if starts with `<` → SQL Server distiller; if starts with `{` or `[` → Postgres distiller; else safety.

- [ ] **Step 2: Run tests, confirm fail**

Run: `dotnet test --filter "FullyQualifiedName~PostgresPlanDistillerTests|FullyQualifiedName~PlanCommandTests"`

Expected: FAIL on new cases.

- [ ] **Step 3: Implement distiller and ExecutePlan detection**

Walk `Plan` / `Plans` recursively. Mapping table from the spec (PhysicalOp ← Node Type, etc.). Truncate predicates to 200 chars. CostFraction = node Total Cost / root Total Cost; if root cost is 0, omit or 0. StatementText omitted unless present (EXPLAIN JSON usually has no SQL — leave `sql` null).

`ExecutePlan`: try detect; on failure return exit 2 with `The execution plan is not a valid SQL Server Showplan document or Postgres EXPLAIN JSON document.` (do not include input).

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/SqlHarness.Core/Postgres/PostgresPlanDistiller.cs src/SqlHarness.Core/SqlHarnessModule.cs src/SqlHarness.Cli/Commands/PlanCommand.cs tests/SqlHarness.Tests/Postgres/PostgresPlanDistillerTests.cs tests/SqlHarness.Tests/Fixtures/seq-scan.explain.json tests/SqlHarness.Tests/Cli/PlanCommandTests.cs
git commit -m "feat: distill postgres EXPLAIN JSON to DistilledPlan"
```

---

### Task 7: counts

**Files:**
- Create: `src/SqlHarness.Core/Postgres/PostgresCounts.cs`
- Modify: `ISqlDialect.CountsCatalogSql` and exact-SQL builder used by `ExecuteCountsAsync`
- Modify: `SqlHarnessModule.ExecuteCountsAsync` to use dialect SQL; keep `CountsQuery` resolution/messages for SQL Server
- Test: `tests/SqlHarness.Tests/Postgres/PostgresCountsTests.cs`
- Keep: `tests/SqlHarness.Tests/CountsTests.cs` unchanged behavior

**Interfaces:**
- Consumes: existing counts flags (`Tables`, `Like`, `Top`, `Exact`), `ResolvedCountSelection` pattern.
- Produces: `PostgresCounts.CatalogSql` using `pg_class.reltuples` (negative → 0) and user namespaces only; `--exact` uses `COUNT(*)` per resolved table; same `SqlHarnessCountsReport`.

- [ ] **Step 1: Write failing SQL-contract and reader tests**

Assert `PostgresCounts.CatalogSql` contains `pg_class`, `reltuples`, `pg_namespace`, and does not contain `sys.dm_db_partition_stats`. Assert it excludes `pg_catalog`, `information_schema`, `pg_toast`, `pg_temp`. Assert `LIKE` is used (not `ILIKE`).

Reader test: fake two-result-set catalog (TotalObjects + rows) then exact COUNT rows, same assertions as `CountsTests` for omitted/ambiguous/missing.

Module test: postgres profile FakeSession first command SQL equals `PostgresCounts.CatalogSql`.

- [ ] **Step 2: Run tests, confirm fail**

Run: `dotnet test --filter "FullyQualifiedName~PostgresCountsTests"`

Expected: FAIL.

- [ ] **Step 3: Implement catalog SQL and module branch**

Unqualified `--table name` joins any non-system schema; more than one `(nspname, relname)` for the same requested name is ambiguous (reuse the existing missing/ambiguous message text from `CountsQuery` if it is already generic; otherwise keep the generic “Requested table was not found or was ambiguous.”).

Exact path: after catalog resolution, `SELECT COUNT(*) FROM schema.table` with identifiers quoted via `quote_ident` in SQL generated from already-validated catalog names (never from raw user strings).

`--like` applies to `c.relname LIKE @like` (case-sensitive).

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/SqlHarness.Core/Postgres/PostgresCounts.cs src/SqlHarness.Core/SqlHarnessModule.cs src/SqlHarness.Core/Dialect tests/SqlHarness.Tests/Postgres/PostgresCountsTests.cs
git commit -m "feat: inventory postgres table counts from pg_class"
```

---

### Task 8: schema

**Files:**
- Create: `src/SqlHarness.Core/Postgres/PostgresSchema.cs`
- Modify: module `ExecuteSchemaAsync` to use dialect SQL; reuse `SchemaReader.ParseObjectSelection` and missing/ambiguous messages
- Test: `tests/SqlHarness.Tests/Postgres/PostgresSchemaTests.cs`

**Interfaces:**
- Consumes: `--object`, `--filter`, `--max-objects`.
- Produces: same `SqlHarnessSchemaReport` / `SchemaObjectReport` from `pg_catalog` (tables+views, columns, PK, indexes with INCLUDE + partial `WHERE`, FKs). Exact match on `nspname`/`relname` as stored.

- [ ] **Step 1: Write failing SQL-contract and module tests**

Assert SQL mentions `pg_class`, `pg_attribute`, `pg_index`, `pg_constraint`, and not `sys.columns`. Module: postgres profile executes `PostgresSchema.Sql`. Object mode with 0 rows → `SchemaReader.MissingOrAmbiguousMessage`.

- [ ] **Step 2: Run tests, confirm fail**

Run: `dotnet test --filter "FullyQualifiedName~PostgresSchemaTests"`

Expected: FAIL.

- [ ] **Step 3: Implement catalog batch and reader**

Result-set order must match what the reader expects (total count, objects, columns, indexes, FKs) so you can share shaping with `SchemaReader` **or** keep a dedicated `PostgresSchema.ReadAsync` that fills the same records. Prefer a dedicated reader; do not fork SQL Server `SchemaReader.Sql`.

Unquoted `--object Contracts` looks for `relname = 'Contracts'` and will miss `contracts`. Document in Task 10, do not lowercase user input.

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/SqlHarness.Core/Postgres/PostgresSchema.cs src/SqlHarness.Core/SqlHarnessModule.cs tests/SqlHarness.Tests/Postgres/PostgresSchemaTests.cs
git commit -m "feat: inspect postgres tables and views via pg_catalog"
```

---

### Task 9: space

**Files:**
- Create: `src/SqlHarness.Core/Postgres/PostgresSpace.cs`
- Modify: module `ExecuteSpaceAsync`
- Test: `tests/SqlHarness.Tests/Postgres/PostgresSpaceTests.cs`

**Interfaces:**
- Consumes: `--top`, `--object`.
- Produces: `SqlHarnessSpaceReport` with Files = one DATA row (`pg_database_size`; `PhysicalName` from `current_setting('data_directory')` when permitted, else null); Allocation `ReservedMb` = database size, `UsedMb`/`DataMb` = sums of `pg_total_relation_size` / `pg_relation_size` for user relations only (they will not sum to Reserved); Tables top N by total size; `--object` indexes with `Type` = AM name and `Compression` = null. Missing object uses `SpaceQuery.MissingOrAmbiguousMessage`.

- [ ] **Step 1: Write failing SQL-contract and reader tests**

Assert SQL contains `pg_database_size` and `pg_total_relation_size`, not `sys.database_files`. Reader: feed five result sets matching SQL Server space’s shape (match count, files, allocation, tables, indexes) so the existing report records populate. `--object` missing → same message.

- [ ] **Step 2: Run tests, confirm fail**

Run: `dotnet test --filter "FullyQualifiedName~PostgresSpaceTests"`

Expected: FAIL.

- [ ] **Step 3: Implement SQL + reader and module branch**

No VACUUM, no AM change. Default `--top` 25 still enforced by the command.

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/SqlHarness.Core/Postgres/PostgresSpace.cs src/SqlHarness.Core/SqlHarnessModule.cs tests/SqlHarness.Tests/Postgres/PostgresSpaceTests.cs
git commit -m "feat: diagnose postgres storage with pg_catalog sizes"
```

---

### Task 10: Playground, docs, opt-in integration

**Files:**
- Create: `scripts/setup-local-postgres.ps1`
- Create: `tests/scripts/setup-local-postgres.Tests.ps1` (mirror AdventureWorks script tests: never prints password, never writes `targets.json`, fail-closed on conflicting resources)
- Create: `tests/SqlHarness.Tests/Integration/PostgresIntegrationFactAttribute.cs`
- Create: `tests/SqlHarness.Tests/Integration/PostgresSessionIntegrationTests.cs` (skipped without env)
- Modify: `README.md`, `AGENTS.md`, `skills/sqlharness/SKILL.md`, `docs/example-targets.json` (add a commented-in-prose postgres example; do not break the existing `prod-eu` object — add a sibling `local-pg` example in README only, or a second key in example-targets if the example file is illustrative)
- Modify: `docs/superpowers/specs/2026-09-10-postgres-engine-design.md` status to `Approved`

**Interfaces:**
- Consumes: AdventureWorks script conventions (fixed names, redacted docker args, no `targets.json` writes).
- Produces: container `sqlharness-pg`, volume `sqlharness-pg-data`, host port **5433**, image `postgres:16`, password only in `SQLHARNESS_PG_PLAYGROUND_PASSWORD`, database Pagila; integration fact watches `SQLHARNESS_PG_INTEGRATION_CONNECTION_STRING`.

- [ ] **Step 1: Write failing Pester tests for the setup script contract**

Follow `tests/scripts/setup-local-adventureworks.Tests.ps1`: the postgres script must not exist yet so tests fail, then implement. Assert the script text contains `sqlharness-pg`, `5433`, `SQLHARNESS_PG_PLAYGROUND_PASSWORD`, `postgres:16`, and does not contain `targets.json` writes (`Set-Content` / `Out-File` to that path).

- [ ] **Step 2: Run Pester (or the existing script-test invocation used in CI)**

If the repo invokes Pester via `dotnet test` only, add a C# test that reads the script file (like other script contract tests if present). `ReleaseWorkflowTests` / `setup-local-adventureworks.Tests.ps1` — match whichever runner already exists. Expected: FAIL until the script exists.

- [ ] **Step 3: Implement script, integration attribute, documentation**

Script behavior:
- Require non-empty `SQLHARNESS_PG_PLAYGROUND_PASSWORD`.
- Create/reuse container and volume; fail-closed if a different container is already using that name/port.
- Wait until `pg_isready`.
- Restore Pagila from the upstream SQL dump into database `pagila` when that database is absent.
- Never print the password or a connection string containing it.
- Never edit `~/.sqlharness/targets.json`.
- Print the manual profile JSON for the user to merge (`engine: postgres`, `server: localhost,5433`, `database: pagila`, `auth: sql`, `trustServerCertificate: true`, `passwordEnvVar: SQLHARNESS_PG_PLAYGROUND_PASSWORD`).

```csharp
internal sealed class PostgresIntegrationFactAttribute : FactAttribute
{
    internal const string Variable = "SQLHARNESS_PG_INTEGRATION_CONNECTION_STRING";
    public PostgresIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"{Variable} is not configured.";
    }
}
```

One integration test: connect via `NpgsqlSessionFactory` against a `ResolvedTarget` parsed from that connection string **or** skip. Do not reuse `SQLHARNESS_INTEGRATION_CONNECTION_STRING`.

Docs (README, AGENTS.md, skill):
- `engine` on profiles; `--engine` only with `--unsafe-direct`;
- Postgres `auth: sql` and SSL mapping;
- native `TEMP` vs `#temp`;
- rejected parameter types;
- EXPLAIN measure + sidecar;
- case-sensitive `LIKE` / identifiers;
- space field analogs;
- playground setup;
- keep all existing SQL Server examples valid; add a `local-pg` example block.

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj`

Expected: PASS, zero live connections.

- [ ] **Step 5: Commit**

```powershell
git add scripts/setup-local-postgres.ps1 tests/scripts/setup-local-postgres.Tests.ps1 tests/SqlHarness.Tests/Integration README.md AGENTS.md skills/sqlharness/SKILL.md docs/example-targets.json docs/superpowers/specs/2026-09-10-postgres-engine-design.md
git commit -m "docs: postgres playground, skill, and opt-in integration hook"
```

---

## Execution notes

- Tasks 1–10 are sequential. Do not start Task N+1 until Task N’s tests pass.
- After each commit, `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj` must be green.
- If SqlParserCS public entry point is `SqlQueryParser` rather than `Parser.ParseSql`, use the installed API; classifier tests stay SQL-in / decision-out.
- Do not add Entra ID, `sslMode`, `#temp` translation, or `pg_stat_statements` in this plan.
