# PostgreSQL transport policy

**Date:** 2026-09-26
**Status:** Accepted. This is a contract change.
**Supersedes:** the SSL mapping and the Postgres server-identity comparison in [2026-09-10-postgres-engine-design.md](2026-09-10-postgres-engine-design.md). That document did not have `sslMode`. It is not rewritten to pretend that it did. SQL Server transport is unchanged.

This policy is S5 and B2 from [the audit](2026-09-26-project-audit.md). It does not add the plan 03 agent envelope (`schemaVersion`, `--output agent`). Legacy use is reported on the existing outcome.

## Fields

Postgres profiles and `--unsafe-direct` accept two optional fields. Password values stay in the environment variable named by `passwordEnvVar`. They are not profile fields and not CLI values.

| Field | Profile JSON | Direct CLI | Absent |
| --- | --- | --- | --- |
| `sslMode` | `sslMode` | `--ssl-mode` | not set |
| `rootCertificate` | `rootCertificate` | `--root-certificate` | not set |

Accepted `sslMode` strings, exact and lowercase, with no surrounding whitespace:

- `verify-full`
- `verify-ca`
- `require`
- `disable`

Any other string, including a different case or an empty string, is rejected. JSON `null` and an omitted property are absent, not an explicit mode.

`trustServerCertificate` keeps its historical meaning. It is not reinterpreted as Npgsql "trust the certificate but keep TLS". On SQL Server it is still SqlClient `TrustServerCertificate`.

## ResolvedTarget

`ResolvedTarget` gains one optional member after `Engine`. Existing positional construction stays valid.

```csharp
public enum PostgresSslMode
{
    VerifyFull,
    VerifyCa,
    Require,
    Disable,
}

public sealed record PostgresTransport(PostgresSslMode Mode, string? RootCertificate);

public sealed record ResolvedTarget(
    string Server,
    string Database,
    AuthSpec Auth,
    string Mode,
    SqlEngine Engine = SqlEngine.SqlServer,
    PostgresTransport? Transport = null);
```

| Input | `Engine` | `Transport` |
| --- | --- | --- |
| Postgres, both new fields absent | `Postgres` | `null` (legacy) |
| Postgres, accepted `sslMode`, rules below | `Postgres` | mode, and the root path or `null` |
| SQL Server, both new fields absent | `SqlServer` | `null` |
| SQL Server, either new field present | rejected before a target is returned | |

`Auth.TrustServerCertificate` is still copied as it is today. It is not folded into `PostgresTransport`. `Transport == null` is the only representation of legacy policy. A Postgres target never stores a synthetic explicit mode for the historical mapping.

The same rules apply to a named profile and to `--unsafe-direct`. On the direct path the two fields are direct options: supplying either one with a profile is rejected with the existing profile-plus-direct error, and supplying either one without `--unsafe-direct` is rejected with the existing direct-options error. Omitted direct fields are legacy, as on a profile.

## Migration matrix

Both `sslMode` and `rootCertificate` absent:

| `trustServerCertificate` | Npgsql `SslMode` | Report |
| --- | --- | --- |
| `true` | `Disable` | `legacy` |
| `false` (default) | `Require` | `legacy` |

No root certificate is added to the connection string. This is today's mapping, including that `true` disables TLS.

An explicit `sslMode` is authoritative. It is rejected when it contradicts `trustServerCertificate`:

| `trustServerCertificate` | `sslMode` | Result |
| --- | --- | --- |
| `false` | `require` | `SslMode.Require` |
| `false` | `verify-full` | `SslMode.VerifyFull` |
| `false` | `verify-ca` | `SslMode.VerifyCA` |
| `false` | `disable` | rejected |
| `true` | `disable` | `SslMode.Disable` |
| `true` | `require`, `verify-full`, or `verify-ca` | rejected |

`false` agrees with `require` because that was the historical false mapping, and it agrees with the two verifying modes because those modes are the new explicit contract rather than a reversal of the disable switch. `true` still means the historical disable request. Combined with any other explicit mode, that bool would again be the switch that turns TLS off, so the combination is rejected. Explicit disable is `sslMode: disable` together with `trustServerCertificate: true`, not a silent reinterpretation of `true` as "encrypt but skip verification".

Contradiction, an unknown mode, a Postgres field on a SQL Server target, and an unusable root file fail with exit 2 before a connection is opened.

## Root certificate

`rootCertificate` is accepted only with an explicit verifying mode (`verify-full` or `verify-ca`). It is rejected when the mode is absent, `require`, or `disable`.

A verifying mode may omit `rootCertificate`. Npgsql then uses its default trust store (`SslVerifyFullValidation` or `SslVerifyCAValidation`). When the path is present it is checked before the password is read:

- the path must be a regular file that can be read as one X.509 certificate (PEM or DER);
- a missing path, a directory, an empty file, or a file that is not a certificate is the same error;
- the error text is fixed and does not contain the path, the file bytes, or the password.

The accepted path is `PostgresTransport.RootCertificate` and Npgsql `Root Certificate`. The builder sets Npgsql `SslMode` only. It does not set Npgsql `TrustServerCertificate`.

## SQL Server

A SQL Server profile, or a direct SQL Server target, that contains `sslMode` or `rootCertificate` is rejected. Omitted `engine` is SQL Server. `trustServerCertificate` alone remains legal on SQL Server. One invalid profile rejects the whole file, as other profile validation does.

## Legacy on the existing outcome

`SqlHarnessTargetIdentityReport` gains an optional `TransportPolicy`. JSON omits it when null (`JsonIgnoreCondition.WhenWritingNull`). SQL Server leaves it null, so existing SQL Server JSON does not grow a field.

Every successful Postgres connection sets it:

- `legacy` when `Transport` is null;
- `explicit` when an accepted `sslMode` was used.

The session identity is the `Target` already embedded in the command report, including `--json` and a `--json-summary` that copies that target. This is not a new envelope. Text mode stays a lossy projection and does not have to repeat the field. `ActualServer` remains the observed identity-query value; it is not the acceptance key below.

## Connected identity reporting and acceptance

Plan 050 supersedes the endpoint-identity acceptance policy above. PostgreSQL no longer inspects Npgsql private connection members or resolves DNS after connecting. SQL Server and PostgreSQL report the server identity observed on the session (`SERVERPROPERTY('ServerName')` and `inet_server_addr()`, respectively), but do not compare that value with the resolved profile host.

Endpoint authentication belongs to the transport: use `sslMode: verify-full` for PostgreSQL hostname verification and `trustServerCertificate: false` for SQL Server certificate validation. Npgsql/SqlClient complete TLS validation during connection establishment according to the configured transport options.

Both engines still compare the connected database (`current_database()` / `DB_NAME()`) with the resolved database using exact ordinal comparison. A database mismatch fails with `SqlTargetMismatchException` and exit 4; a server-name or address difference alone does not fail. PostgreSQL `transportPolicy` remains reported as `legacy` or `explicit` as described above.

Unit tests use a fake observed server identity for PostgreSQL; no live database is needed to prove that an endpoint string difference no longer gates acceptance. TLS handshake behavior remains owned by the providers and transport configuration.
## Errors and secrets

Safety errors use these fixed messages:

- `Postgres sslMode is invalid.`
- `Postgres sslMode contradicts trustServerCertificate.`
- `Postgres rootCertificate requires a verifying sslMode.`
- `Postgres rootCertificate is missing or unreadable.`
- `SQL Server targets cannot set Postgres transport fields.`

None of them includes the password, the rejected mode string, the certificate path, or certificate bytes. The missing-password exception may still name the variable: `SQL password environment variable '<name>' is missing or empty.`

## Opt-in TLS proof

`SQLHARNESS_PG_TLS_PROOF` is the only switch for the live proof. When it is unset or whitespace, the proof tests skip. When it is set, it is a path to a JSON file for one authorized disposable target. The tests refuse `~/.sqlharness/targets.json` (the resolved `SqlHarnessPaths.TargetsFile`) and refuse `passwordEnvVar` equal to `CIVICLENS_POSTGRES_PASSWORD`. They do not read user profiles.

```json
{
  "host": "db.example.test",
  "port": 5432,
  "database": "proof",
  "user": "proof",
  "passwordEnvVar": "SQLHARNESS_PG_TLS_PROOF_PASSWORD",
  "trustedRootCertificate": "ca.pem",
  "untrustedRootCertificate": "untrusted.pem",
  "badHost": "wrong.example.test",
  "plainHost": "192.0.2.10"
}
```

`plainHost` is an IP literal. `host` is the DNS name on the server certificate. The machine trust store must not already trust that certificate; only `trustedRootCertificate` does. The server accepts non-TLS on `plainHost`. `host` resolves to the address that the verifying connection actually uses. The password is read only from `passwordEnvVar`.

The proof opens four connections with a 5 second connect timeout:

| Case | Target | Expected |
| --- | --- | --- |
| Bad host | `badHost`, `verify-full`, trusted root | failure, no session |
| Untrusted CA | `host`, `verify-full`, untrusted root | failure, no session |
| Trusted CA | `host`, `verify-full`, trusted root | success, database matches, `transportPolicy` is `explicit` |
| No TLS | `plainHost`, `disable`, `trustServerCertificate: true` | success, database matches, `transportPolicy` is `explicit` |

Failure exceptions must not contain the password. This proof is not part of the default suite.

## Non-goals

- SQL Server certificate validation and the historical meaning of PostgreSQL `trustServerCertificate` when `sslMode` is absent remain transport-policy concerns.
- No agent output envelope, and no edit to `skills/sqlharness/SKILL.md`.
- No claim that a DNS answer, a proxy, or `require` / `disable` authenticates the server.
- No rewrite of the 2026-09-10 design as if `sslMode` had always been there.
