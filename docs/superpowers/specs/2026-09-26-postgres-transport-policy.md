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

## Endpoint identity

Postgres does not use `SqlExecution.TargetMatches`. That method remains the SQL Server check, including `.`, `(local)`, the Azure suffix, and loopback. Deleting the Postgres check is not the fix.

The database is always checked, for every mode, including loopback: `current_database()` must equal the resolved database with `StringComparison.Ordinal`. A mismatch fails even when no resolver is called.

The endpoint check uses the TCP connection that was established, plus TLS authentication when the mode verifies the host. It does not compare the profile host as text with `inet_server_addr()`. That function can be an IP, an IP with a CIDR suffix, another interface, or the origin behind a proxy, and a DNS name never matches it. The identity statement is unchanged and its server column is only `ActualServer` in the report.

Postgres loopback is only `localhost` (ordinal ignore case), `127.0.0.1`, `::1`, and `[::1]`, after the `,port` suffix is removed. `.` and `(local)` are not Postgres loopback. Loopback skips the endpoint check and does not call the resolver. The database check still applies. The port is not an identity field.

Host form is `host` or `host,port`. An IPv6 host is written `[address]` or `[address],port`. Brackets are removed before IP parsing. One trailing DNS root dot is not ignored.

Non-loopback rules:

| Effective mode | DNS name | IP literal |
| --- | --- | --- |
| `verify-full` | Passes only when the host is authenticated and the connected address is in the resolver answer | Same, using IP equality for the authenticated name and the resolver answer |
| `verify-ca` | Same name check as `verify-full`. `verify-ca` itself does not authenticate the hostname, so a certificate whose names do not include the requested host fails | Same |
| `require`, `disable`, and legacy (`Require` or `Disable`) | Fails. A resolver hit is not authentication | Passes only when the connected address equals the requested address |

Effective mode is the explicit mode, or the legacy mapping when `Transport` is null.

Authenticated names come from the peer certificate when it can be read: DNS and IP subject alternative names, or the subject CN only when the certificate has neither. Comparison is ordinal ignore case. A name `*.example.test` matches one label (`db.example.test`) and does not match `example.test` or `a.b.example.test`. IP names use `IPAddress.Equals`. IPv4-mapped IPv6 is not folded into IPv4.

`verify-full` already requires Npgsql to complete hostname verification for the requested host. If the certificate cannot be read after that handshake, the only authenticated name is that requested host. `verify-ca` does not get that fallback: no readable names means failure. A different authenticated host fails even when the connected address is in the resolver answer.

The resolver is called only for a non-loopback verifying check. It receives the host with the port and brackets removed. An IP literal is returned as that address and is not sent to DNS. A DNS failure, a missing connected address, or any other resolver exception fails the check. The mismatch exception has no inner exception.

Proxy and address change: there is no proxy allow-list. The connected remote address must be one of the addresses the resolver returns after connect. A proxy address that the requested name does not resolve to fails, even if the certificate name matched. `inet_server_addr()` is not a substitute. If DNS no longer contains the connected address, the check fails.

DNS by itself is not authentication. `require` and `disable` reject a DNS name even when the resolver returns the connected address.

A failed check throws the existing `SqlTargetMismatchException` (`Connected SQL target identity does not match the resolved target.`) and the session is disposed. The module already maps that to exit 4. If the connected address or a required certificate cannot be read, the check fails closed rather than falling back to string equality.

Unit tests supply a resolver and endpoint metadata. They open no sockets. The production resolver is `Dns.GetHostAddresses` only for a non-IP host on a real verifying connection.

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

- No change to SQL Server `TargetMatches`, SqlClient encryption, or the historical meaning of `trustServerCertificate` when `sslMode` is absent.
- No agent output envelope, and no edit to `skills/sqlharness/SKILL.md`.
- No claim that a DNS answer, a proxy, or `require` / `disable` authenticates the server.
- No rewrite of the 2026-09-10 design as if `sslMode` had always been there.
