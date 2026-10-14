# Mutation approval through the dashboard — design

**Status:** open questions resolved by the operator on 2026-10-14 (§11). No implementation plan has been written yet. Supersedes the approval model of the [Windows tray spec](2026-09-26-windows-approval-tray-design.md). The native tray stays deferred: it is another app to build and ship on three operating systems.

**Inputs:** the operator's decisions of 2026-10-14 and a security review by Opus, Codex (gpt-6.1-sol) and Grok. The tray spec's state machine, digest and outcome rules are reused below where they still fit.

## 1. Goal and threat model

The feature prevents accidents. A typical case: an agent drops a table or rewrites every row because it forgot a semicolon or a `WHERE`, then apologises. Before any persistent change runs, the operator sees the exact batch and its target, or a per-profile policy decides on the operator's behalf.

It is **not** a boundary against a determined agent running as the same OS user. Such an agent can:

- read password variables and `~/.sqlharness` files;
- call the dashboard API;
- drive the browser;
- reach the database without SQLHarness.

The README must say this, as it already does for the CLI. Optional WebAuthn (§6) and a separate writer credential (§5) raise the bar for accidental or casually injected self-approval. They do not create isolation.

Out of scope:

- adversarial isolation (separate OS identity, phone confirmation);
- an approval-for-the-whole-session grant;
- the external-model `auto` mode (Jev). Auto gets its own later plan on top of this one.

## 2. Interface (CLI/MCP parity)

| | CLI | MCP |
|---|---|---|
| Read-only work | `query` (unchanged, never mutates) | `sqlharness_query` (unchanged) |
| Persistent change | `mutation` (new) | `sqlharness_mutation` (new) |

Removed: `query --allow-mutation` and `--confirm-database`.

`mutation` accepts the same target, SQL-source, parameter, timeout and output options as `query`, plus:

- `expectDatabase` (CLI `--expect-database`): optional. When it is present and differs from the database the broker resolves (ordinal comparison), the request is rejected before anyone is asked. It replaces `--confirm-database` as a cheap check against the wrong profile or variables.
- `dryRun` (CLI `--dry-run`): the same offline checks as other dry runs, plus the tier from §3 and the mode the profile would apply. It never contacts the dashboard and never authorizes anything. Every dry-run report gains a `tier` field.

`mutation` with a batch that has no persistent effect is rejected (`not_a_mutation`). Use `query` for that.

Parity rule, added to README next to the existing removal rule: a command added to one interface is added to the other.

## 3. Risk tiers

Core classification moves from a single `Unsupported` bucket to tiers in both engines. A batch's tier is the highest tier of its statements.

| Tier | Contents |
|---|---|
| `session` | Today's session-local work: `#temp`, PostgreSQL proven TEMP, table variables. It never needs approval, in either command. |
| `dml` | INSERT, UPDATE, DELETE and MERGE on persistent tables. |
| `schema` | Persistent DDL: CREATE/ALTER/DROP of tables, indexes, views, routines, triggers, schemas, types and sequences; persistent `SELECT … INTO`; `TRUNCATE` of a persistent table; sequence advances. |
| `privilege` | GRANT, REVOKE, DENY, role membership, CREATE/ALTER/DROP of users and roles. It is **rejected** when it grants rights to, or adds role membership for, the reader principal. The reader principal is the identity the agent's own connections use. The broker resolves it at execution time by connecting with the profile's reader credential: `SUSER_SNAME()` and `USER_NAME()` on SQL Server, `current_user` and `session_user` on PostgreSQL. The rule therefore holds whatever the setup: a dedicated reader login, or the operator's own Entra account that agents share at work. |
| `opaque` | `EXEC`/`CALL` of procedures, dynamic SQL (`sp_executesql`, `EXEC(@sql)`, PostgreSQL `EXECUTE`/`DO`). |
| rejected | Everything still unsupported, at any approval: server and instance scope (ALTER SERVER/DATABASE options, CREATE/ALTER LOGIN, BACKUP/RESTORE, SHUTDOWN, KILL, DBCC, RECONFIGURE, `xp_cmdshell`), cross-database names, linked servers and `dblink`/FDW, `COPY … PROGRAM`, unknown syntax. |

The `privilege` and `opaque` tiers are deliberate capability expansions. Their execution semantics are §5's.

Known residual effect, documented and not fixed here: PostgreSQL TEMP setup may call a user function that writes persistently, without approval. This is unchanged from today (README PostgreSQL execution).

## 4. Approval modes

Modes are configured per profile in `targets.json`, with a global default in `config.json` (`mutations.defaultMode`, default `never`):

```json
{
  "dev": {
    "server": "...", "database": "...", "vars": {}, "auth": "sql", "sqlUser": "app_reader", "passwordEnvVar": "SQLH_DEV_PASSWORD",
    "environment": "dev",
    "mutations": { "mode": "yolo", "writer": { "sqlUser": "app_writer", "passwordEnvVar": "SQLH_DEV_WRITER_PASSWORD" } }
  },
  "work": {
    "server": "...", "database": "...", "vars": {}, "auth": "azure-cli",
    "mutations": { "mode": "operator", "writer": { "sameAsReader": true } }
  }
}
```

| Mode | `dml` | `schema` / `privilege` / `opaque` |
|---|---|---|
| `never` | `mutations_disabled` | `mutations_disabled` |
| `operator` | operator decides | operator decides |
| `yolo` | executes without asking | operator decides |

- `environment` is a free label shown in large type in the approval view.
- `yolo` on a profile labelled `prod`, `production` or `staging` (case-insensitive) needs `"acknowledgeProductionYolo": true` in its `mutations` block. Without it the profile fails to load with a clear error. This catches the common accident: a `yolo` dev profile copied and pointed at production.
- While such a profile exists, the dashboard shows a persistent warning banner naming it. History marks every change on it that ran without asking.
- The label is voluntary and the operator's call. It does not follow the database a profile variable resolves to, so a profile whose variables switch between dev and production keeps one label. README documents that risk.
- `operator` and `yolo` require a `writer` block (§5).
- The broker reads modes when a request arrives, not at process start. Changing a profile's mode affects the next request.

## 5. Execution: the dashboard is the broker

The dashboard is the only process that executes persistent changes:

1. `mutation` (CLI or `mcp serve`) classifies offline, then submits the frozen request to the dashboard over loopback HTTP.
2. The dashboard independently resolves the profile and re-classifies.
3. It decides according to the mode, or waits for the operator.
4. It executes through Core's query path with the profile's **writer credential**, `AllowMutation` set internally and the database check pinned to the database it resolved.

Callers never receive the writer credential, and nothing a caller sends can set "approved".

Writer credential:

- Default: a user name plus a password variable read only by the dashboard process.
- Entra or Azure authentication: a separate token identity is a later extension. Until then, any profile may set `"writer": { "sameAsReader": true }`, which the approval view flags.
- Docs recommend an account that differs from the reader principal, and a reader account without write rights. The agent shares the user's environment, so this is hygiene, not secrecy (§1).

The request is frozen as one value:

- exact SQL text;
- typed parameter names and values;
- profile, variables and resolved engine/server/database;
- tier and timeout;
- an expiry.

A SHA-256 digest of it identifies the request. What the operator sees is what executes. Changing a file afterwards changes nothing.

The dashboard auto-starts when it is not running (existing setting). If it cannot start, the call returns `approval_unavailable` at once.

## 6. Operator flow and verification

Notifications are delivered by SSE to open dashboard tabs:

- an in-page toast;
- a browser Web Notification when the tab is in the background (permission is requested once from Settings).

The notification text is generic ("SQLHarness: a change needs your decision"), with no SQL, names or values. It opens the approval view and approves nothing.

The approval view shows:

- environment, profile, resolved server and database;
- tier and statement kinds;
- target objects;
- the full SQL, scrollable, rendered as text;
- every parameter value;
- the digest and the time left;
- a warning for multi-statement batches: "a failure can leave earlier statements committed".

Buttons are **Approve** and **Deny**. Enter does not approve. A truncated or oversized preview cannot be approved.

Approval verification is a dashboard setting, `mutations.verification`:

- `click` (default): Approve is enough.
- `webauthn`: Approve runs a WebAuthn ceremony on the dashboard origin. The challenge is bound to the request digest. User verification is preferred (Windows Hello, Touch ID, or a security key with PIN or biometrics). Presence-only security keys are allowed when the operator registers them as such.
  - Enrolment happens in Settings. Adding a second credential requires an assertion from an existing one.
  - Switching back to `click` requires an assertion.
  - A lost authenticator is recovered by deleting the credential file by hand (documented).

One pending request is allowed per caller session, plus one executing mutation per profile. A repeat of a request with an identical digest while one is pending joins it and raises no second toast. A repeat after `denied_by_operator` within 60 s returns the same denial without asking.

## 7. Waiting, outcomes and result codes

The `mutation` call blocks until there is a decision or the approval TTL ends:

- TTL default 120 s, operator setting 30..600.
- `mcp serve` sends MCP progress notifications while waiting. The CLI prints a waiting line to stderr.
- One atomic transition per request (`pending → approved | denied | expired | cancelled`) arbitrates a click that races the deadline. A click after expiry runs nothing.
- If the caller disconnects before execution starts, the request is cancelled. After execution starts, it finishes and its outcome is journaled.

| Code | Meaning |
|---|---|
| `success` | Executed; the result uses the `query` envelope (result sets, `recordsAffected`, messages, bounded output). |
| `mutations_disabled` | Mode `never`. |
| `approval_unavailable` | The dashboard could not be reached or started. |
| `approval_timeout` | No decision before the TTL. |
| `denied_by_operator` | The operator refused. |
| `busy` | A mutation is already executing on this profile, or the per-session pending limit was hit. |
| `not_a_mutation`, `database_mismatch` | Rejected before anyone was asked. |
| `principal_protected` | A `privilege` batch names the reader principal (§3); nothing executed. |
| `failed` | SQL error after the start; effects of earlier statements may remain. |
| `outcome_unknown` | Connection lost after sending; effects may have occurred. |

Exit codes reuse the existing table: safety and approval rejections `2`, mismatch `4`, SQL failure `5`. `outcome_unknown` is `5` with a distinct status.

Skill rule: on any code other than `success`, stop and report to the user. Never retry automatically and never switch to another transport. Server-side, a request never executes twice, and an approval is consumed before execution starts.

## 8. Journal and dashboard history

Every request is journaled with:

- caller session, profile, resolved target;
- tier, digest and mode;
- decision, verification method and outcome;
- timestamps.

SQL text and parameter values follow `journal.storeSensitive`, as operations already do. A dashboard "Mutations" view lists the history. The journal is evidence, not authority: the broker never reads a decision back from SQLite.

## 9. Agent-side hygiene (documented, optional)

- Extend `scripts/hooks/protect_sqlharness_config.py` to cover `dashboard-token`, `config.json` writes, `targets.json` writes and loopback calls to the dashboard approval endpoint across Claude Code and Codex tool names.
- Recommend blocking `127.0.0.1:47800-47899` in browser-automation site permissions.

These measures reduce accidents; per §1 they are not a security boundary.

## 10. Capabilities, docs, skill

- Capabilities report, for the active scope:
  - the profile's mode;
  - whether a writer credential is configured (never the credential itself);
  - the verification setting;
  - the TTL;
  - whether the dashboard is reachable.
- README:
  - replace the Mutations section;
  - safety contract and the honest threat model;
  - parity rule;
  - remove `--allow-mutation`.
- `docs/mcp.md`: replace "No persistent mutations" with the mutation tool contract; update the tool count and catalog budget.
- Skill: use `query` for reading and `mutation` for persistent changes; the stop-and-report rule; never put mutation SQL in `query`.
- AGENTS.md "Writes" bullet: the approval now lives in the operator's dashboard, not in agent-supplied flags.

## 11. Review decisions (operator, 2026-10-14)

1. The reader principal for the `privilege` rule is resolved at execution time (§3). At work, agents often connect with the operator's own Entra account, so the principal depends on how the profile is configured.
2. `yolo` never auto-approves `schema`, `privilege` or `opaque`, whatever the environment label. Those always ask.
3. A closed browser means the notification goes unseen and the call ends in `approval_timeout`. This is accepted for v1. The deferred tray remains the later fix.
