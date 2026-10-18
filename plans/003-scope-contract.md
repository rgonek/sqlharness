# 003/T1 — MCP scope model for artifacts (contract)

Status: **contract (docs-only, T1)**. Documentation-only step: zero production-code
and test changes. Implementation (T2–T5) must realize exactly this model.

## 1. Scope definition (what constitutes owner identity)

MCP scope = a tuple frozen in `McpScope` at process start:

- `TargetRequest`: `Profile` (profile name) + `Vars` (canonical var set,
  a `StringComparer.OrdinalIgnoreCase` dictionary, rejects empty keys and null values;
  `src/SqlHarness.Mcp/McpScope.cs`, `SqlTargetRequest` in `src/SqlHarness.Core/Contracts.cs`).
- `ResolvedTarget`: `Server`, `Database`, `Auth`, `Mode`, `Engine`
  (`SqlEngine.SqlServer` by default), `Transport` (`PostgresTransport` for Postgres);
  `src/SqlHarness.Core/Targets/TargetResolver.cs`.
- Canonical var set = the normalized form of `Vars` from `TargetRequest`
  (case-insensitive keys, order sorted when serialized into metadata;
  owner comparison by key=value pairs, not by CLI argument order).

Artifact owner = that tuple in data form:

```text
owner = { profile, vars-kanoniczne, engine, server, database }
```

Without passwords and secrets: `Auth`/`AuthSpec` (user, strategy, `passwordEnvVar`) and
environment variable values NEVER reach the owner metadata nor the reports. Secrets stay
in the process environment and in the operator's profile store
(`docs/mcp.md`: "Connection secrets stay in the process environment...").
The name of the environment variable holding the password may be part of `AuthSpec`, the value itself — never.

What must NOT be used as authorization (plan stop condition):

- the database name alone (`Database` without `Server` + `Engine` + profile/vars),
- the artifact directory name alone (the id is an opaque directory segment, not an access right),
- the SQL text or the report file name.

## 2. Where the owner is written (chosen variant)

Chosen variant (verbatim from the task): **versioned owner metadata created
through a trusted write path; a missing owner means MCP rejection.**

Concretely, grounded in today's code:

- `ArtifactManifest` v1 (`src/SqlHarness.Core/ArtifactReader.cs`):
  `CurrentManifestVersion = 1`, fields `ManifestVersion`, `ArtifactKind`, `ReportFile`,
  `Sections`. Kinds: `compare`, `measure`, `measure-set` (`CompareKind`,
  `MeasureKind`, `MeasureSetKind`). Sections: `summary`, `metrics`, `operators`
  (`SummarySection`, `MetricsSection`, `OperatorsSection`). Writer:
  `ArtifactManifest.ForReport` + `ArtifactDirectoryPublisher` (`src/SqlHarness.Core/Artifacts.cs`).
- `SnapshotDocument` v1 (`src/SqlHarness.Core/SnapshotStore.cs`):
  `Version`, `CreatedAt`, `ResultSets`, `ResultHash`, `CurrentVersion = 1`.
  Writer: `SnapshotDocument.Create` via `SnapshotRunner` (`src/SqlHarness.Core/SnapshotRunner.cs`).

T3/T4 contract (to be implemented, not in this step):

- Owner fields are **strictly additive**: existing manifest/snapshot document
  fields stay unchanged; a versioned owner field is added (e.g. bumping
  `ManifestVersion`/`Version` while preserving backward reading of v1 by CLI).
- Trusted write path = the existing Core writer (`ArtifactDirectoryPublisher`,
  `SnapshotStore.Save`): it is the Core writer, not the MCP or CLI adapter, that
  writes the owner metadata from the operation context it received from the frozen scope.
- Owner enforcement as **opt-in per call** (controller ruling, binding):
  an `owner` parameter in the Core reader/mapper (`ArtifactReader.ReadSection`,
  `McpOperationMapper.ReadArtifactSection`); MCP passes the owner from the frozen scope,
  CLI does not pass one and keeps name-based behaviour. Core remains the shared
  safety locus without per-adapter branches.
- **Missing owner = MCP rejection**: a legacy artifact (a directory without a manifest or
  a v1 manifest without an owner field, a v1 snapshot without a target) is readable by CLI
  (the existing deliberate name-based behaviour, offline reader unchanged), but MCP refuses
  before returning any data — a safe error without content, `isError=true`.
  MCP must not grant itself a right to an old artifact on the basis of its name
  (self-granting prohibited, T2 test).
- Integrity boundary: **a local writer with process permissions is outside the integrity
  boundary** — the manifest is metadata for enforcing scope within a single
  trusted host, not cryptographic proof. We do not promise resistance
  to manifest forgery by a local writer (manual editing of `manifest.json` /
  `report.json` under `SqlHarnessPaths.CompareDir` or of a snapshot file). The contract
  guarantees only: without an owner matching the MCP scope it refuses; with a mismatching
  owner MCP refuses; a matching owner + matching sections = a projection
  of `summary`/`metrics`/`operators` without raw SQL, plans, `queries.jsonl` and cells.

## 3. Persistence across restart

- `McpScope` is immutable and frozen for the process: the profile is read once,
  the target is resolved once, no reload (`McpScope.Create`, `docs/mcp.md`
  "Scope lifetime"). A new profile/vars/target requires a process restart.
- The artifact owner is **persistent**: written into the manifest/snapshot document
  next to the artifact, so an own artifact works after restart — a new process with the same
  profile + canonical vars + resolved `engine`/`server`/`database` satisfies
  the owner comparison without any server-side in-memory state.
- The post-restart comparison relies solely on metadata data (profile, vars,
  engine, server, database); passwords are never recovered from an artifact.
- Legacy (missing owner field) after restart behaves exactly as before:
  CLI reads, MCP refuses. We do not migrate nor automatically overwrite
  existing files (T4).

## 4. Owner for matrix and param-set

- `compare --matrix`: one dimension, at least two typed values, the user's order,
  **a new connection and one setup per value**, the first error stops the
  run, completed cells remain (`CompareCellRunner`: `ResolvedTarget Target` per
  cell). Owner: each cell inherits the owner of the invocation scope
  (the same profile/vars/engine/server/database; only the matrix value differs,
  and that is an execution parameter, not part of the scope identity). MCP reading of the whole
  matrix requires the scope owner; partial (`status: partial`) does not widen access.
- `measure --param-set`: **one session, one setup** (setup binds the first set),
  strict `.sqljson` files (`name` + `parameters` only, ≤64 KiB, without BOM/comments),
  rotation of measurements `r modulo setCount`. Owner: one scope owner for the
  whole `measure-set` artifact; the individual sets (`name`, value hash, types)
  are internal data, not separate owners. Per-set stability is not
  a cross-set equivalence claim.
- Both modes: matrix parameters/values never reach the owner metadata
  nor the reports (`Values are never copied into reports`); owner is scope, not data.

## 5. Read contract (what reads, what refuses)

Today's defect (reference point, verbatim):

```text
return ArtifactReader.ReadSection(SqlHarnessPaths.CompareDir, id ?? string.Empty, section);
```

`ReadArtifactSection(scope, id, section)` accepts a scope but does not check it —
it reads the global `CompareDir`. Target state (T3): an owner check **before the report
projection**, in Core, on the `ArtifactReader.ReadSection(root, artifactId, section)`
path with an opt-in `owner`. Limits remain: id = a single directory name tied to a manifest
(≤128 characters, pattern, no traversal/links), manifest ≤64 KiB (`MaxManifestBytes`),
report ≤16 MiB (`MaxReportBytes`), only sections from the manifest mapping, errors without content
(`ArtifactReadException`, exit `Safety`/`LocalStorage`).

Snapshots (T4): capture/diff separately. MCP requires an owner and rejects a foreign scope;
CLI keeps name-based behaviour. `--diff` never prints cell values
(only locations/kinds); exit `8` on differences, `7` for watch-timeout — both
preserved.

## 6. Global binding restrictions (immutable in this plan)

- Exit codes `0/2/3/4/5/6/7/8` preserved; legacy JSON preserved; both engines
  (`sqlserver`/`postgres`, `SqlEngine`) preserved.
- Fields only additive (manifest, snapshot, reports, MCP envelope).
- Core the shared safety locus; adapters (MCP/CLI) do not create their own classifier.
- Tests: existing fake reader/session/module patterns and a synthetic
  `SQLHARNESS_HOME`; synthetic reports, not user artifacts.
- Do not read real secrets or user artifacts; `.sqlplan`,
  `queries.jsonl`, snapshots and runtime parameters are locally sensitive.

## 7. Source map (worktree, base 637a59b)

- `src/SqlHarness.Mcp/McpScope.cs` — frozen scope: `TargetRequest`, `ResolvedTarget`, vars.
- `src/SqlHarness.Core/ArtifactReader.cs` — `ArtifactManifest` v1, `ReadSection`, limits.
- `src/SqlHarness.Core/SnapshotStore.cs` + `SnapshotRunner.cs` — `SnapshotDocument` v1, write/read.
- `src/SqlHarness.Core/Artifacts.cs`, `Contracts.cs` — `ArtifactDirectoryPublisher`, reports,
  `SqlTargetRequest`, `SqlHarnessExitCode`.
- `src/SqlHarness.Core/Targets/TargetResolver.cs` — `ResolvedTarget` (Server/Database/Auth/Mode/Engine/Transport).
- `src/SqlHarness.Mcp/McpOperationMapper.cs` (`ReadArtifactSection`),
  `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` — today's path without a check.
- `docs/mcp.md` (Scope lifetime, Artifacts), `AGENTS.md` (engine, exit codes).
- Matrix: `CompareCellRunner.cs`; param-set: strict `.sqljson`, one session.

## 8. T1 acceptance criteria

- [x] `plans/003-scope-contract.md` exists and contains the model from §1–§7.
- [x] `git diff --check` → exit 0; the only change is the new `plans/003-scope-contract.md`.
- [ ] Code regression (T2): the old code does not pass the new foreign-scope/legacy tests
      — out of T1 scope, to be performed in T2.
