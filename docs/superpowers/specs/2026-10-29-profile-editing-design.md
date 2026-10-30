# Editable profiles and inherited mutation policy

Status: DONE on `plan-124`; Windows-only profile Save. Task, whole-branch and final scoped reviews approved; final Windows gate passed on product `71a42d2a`. No merge, push or live/Linux acceptance.
Inspected SQLHarness HEAD: `455adea0` (2026-10-29).

## Goal and scope

Let the operator edit an existing profile in Dashboard Profiles, including its connection,
variable rules, environment, mutation-policy inheritance and writer configuration.
Editing is an explicit local settings action, not approval to execute SQL.
Creating, deleting and renaming profiles, editing secrets, connection tests, automatic restarts
and live SQL are out of scope.

## Current evidence

- `DashboardProfiles.Read` projects targets.json read-only. Its view falls back to never without
  consulting global mutation settings; capabilities and the executor consult the global default.
- `ProfileMutations.Mode` is currently required. `ProfileStore.ValidateMutations` requires a writer
  whenever a profile explicitly enables mutations. Absence of the whole block inherits the mode,
  but leaves no way to configure a writer independently of that choice.
- `MutationExecutor` reloads profiles before executing, rejects changed targets, and requires a writer.
  A writer may explicitly reuse the reader identity; it does not grant SQL privileges.
- Dashboard Settings already supplies a guarded local write and atomic config-file replacement.
  Existing MCP processes retain startup profile/configuration state; a disk save is not live reload.

## Chosen design

Use the existing Profiles page and a typed form rather than a raw whole-file JSON editor.
Use absence of profile mode for inheritance rather than copying the current global value.
A copied value would stop following future global changes and obscure the source of policy.

### Editable fields

Keep the profile name immutable. Edit engine, server, database/template, auth strategy,
SQL user and password environment-variable name, engine-appropriate TLS fields, variable
names/rules, environment, mutation mode, writer kind and production-YOLO acknowledgement.
Passwords and environment values are never read, submitted or displayed.
Validate engine/auth/template/variable/TLS combinations through existing runtime validators;
do not invent a second, more permissive connection model.

### Mutation contract

`ProfileMutations.Mode` becomes nullable/optional. Missing/null means inherit
`config.mutations.defaultMode`; explicit values remain never, operator or yolo.
An absent mutations block remains backwards compatible and inherits mode without a writer.

Writer is an independent profile choice: unconfigured, sameAsReader, or separate SQL user plus
password environment-variable name. No global writer exists and none is introduced.
Switching to inherited mode must retain an existing writer and acknowledgement.
Switching writer kinds removes inactive credential fields after explicit Save.
Global mode changes must never silently enable sameAsReader or create credentials.
Keep existing explicit operator/yolo writer validation; inherited enabled mode with no writer
remains loadable and displays a blocking readiness warning rather than being called executable.

For example, a synthetic profile can store
`"mutations": { "writer": { "sameAsReader": true } }` and inherit the global mode.
Explicit never overrides even a globally enabled mode. Display global mode, configured choice,
effective mode, policy source, writer readiness and acknowledgement separately.
For inherited yolo on prod/production/staging, require explicit acknowledgement before DML can
execute; fail closed without it, including when the global default changes after the profile save.
YOLO bypasses the operator click only for DML; schema, privilege and opaque tiers still require it.

### Save and concurrency

Saving existing profiles is supported on Windows only, where replacement preserves required security metadata. Other platforms refuse saves before file access and report editing availability through GET.

GET /api/profiles keeps its existing fields and adds revision and editable profile data.
Add a guarded PUT /api/profiles accepting
`{ name, expectedRevision, profile }`; name identifies an existing profile only.
The revision is an opaque SHA-256 of the exact current file bytes, not a timestamp.
No endpoint accepts a filesystem path. Reuse the dashboard Host/Origin/cookie/custom-header guards.

Update one profile while preserving all others and validate the complete candidate document.
Missing/invalid documents are read-only; this flow does not repair or overwrite them.
Reject stale revisions with 409 and retain the user's form; field errors use 400,
missing profile uses 404, I/O failure uses a fixed safe 500 without file or secret content.
Reject unknown request members, malformed/duplicate fields and bodies above 1 MiB (HTTP 413).
Use a process-local writer lock, revision recheck immediately before replacement and an atomic
same-directory temp/replace. Clean up failed temp files and retain the original on failure.
This handles dashboard concurrency and detects prior external edits; it is not an OS-level CAS
guarantee against a noncooperating external writer racing the final replacement.
Do not serialize an entire stale client-side profile collection or reformat unaffected profiles
by rebuilding the document from a lossy view model.

### UI and runtime behavior

Read view gains Edit; form offers Save and Cancel, field errors and an unsaved-change guard.
Changing selection with a dirty form requires discard confirmation; Cancel writes nothing.
Show an effective-policy preview and a missing-writer explanation before Save.
Display save success and the need to restart/reconnect existing MCP clients for refreshed profiles;
never restart services or run a connection/query automatically.
Dashboard mutation execution continues to reload and recheck current target/policy.
A pending approval for a changed target must not execute against the new target.

## Acceptance

Synthetic tests cover round-trip editing, inherited/explicit modes, writer independence, global
mode changes, inherited production YOLO, stale writes, guarded endpoints, secret non-access,
atomic failures, untouched sibling profiles, UI cancellation and retained dirty form on conflict.
Existing explicit-mode profile files, mutation approval behavior and CLI/MCP contracts remain valid.
No persistent database write or operator approval is needed for these synthetic tests.

