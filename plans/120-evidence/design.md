# Task 2 — safe dispatch study design

## Candidate v1 grammar

The simulator accepts an UTF-8 JSON object with exactly the study-level fields `version` and `comparisons`. `version` is integer `1`; `comparisons` is an ordered array with 1–16 entries. The source manifest file must be at most 1 MiB in raw UTF-8 bytes, measured before parsing. Each comparison has exactly these fields:

```json
{
  "id": "unique-stable-name",
  "surface": "mcp",
  "profile": "operator-selected-profile-name",
  "vars": { "dataset": "synthetic-value" },
  "subject": "logical-query-subject",
  "scope": "bounded-experiment-scope",
  "baseline": { "path": "sql/base.sql", "sha256": "64 lowercase hex digits" },
  "candidate": { "path": "sql/candidate.sql", "sha256": "64 lowercase hex digits" },
  "setup": null,
  "repeat": 5,
  "timeoutSeconds": 30,
  "compareResults": "ordered",
  "compareNullability": false,
  "input": { "kind": "parameterSetFiles", "files": [ { "path": "cases/a.sqljson", "sha256": "64 lowercase hex digits" } ] }
}
```

Required entry properties are shown above. `setup` may instead be a file reference. `vars` is an explicit object and may be empty. IDs are unique ordinal strings. Profile, variables, subject, and scope are data only: they are never resolved to a profile, credential, endpoint, command, or live target. `repeat` and `timeoutSeconds` are integers 1–100 and 1–300. Result policy is one of `ordered`, `multiset`, `set`, or `off`; `compareNullability` must be a JSON boolean and cannot be true with `off`.

The only accepted input forms in this study are:

- `fixed`: `parameters` is an explicit array of typed scalar parameters. It maps to the existing fixed-parameter compare shape. No inline shell or expression is accepted.
- `parameterSetFiles`: `files` is a non-empty ordered array of exact-byte references to named set files. This shape is MCP-only in the current contract. The CLI compare command does not accept `--param-set` or named set files.
- `matrix`: deferred. CLI compare's single text matrix and MCP's typed matrix do not have identical value grammars (notably empty text and typed NULL). The simulator rejects this kind until a surface-specific grammar is separately specified.

The executable mock validates scalar value shapes, a 2,100-parameter per-case ceiling, 100,000 materialized scalar values per operation, and a 32 MiB materialization ceiling. It parses the ordinary JSON emitted by the synthetic generator only; it does not reproduce the existing reader's BOM/comment/trailing-comma behavior or complete typed parameter grammar. Those semantics are deferred to the existing production reader/resolver and are not supported by this feasibility result.

`fixedParameterFile`, `parameterSetFile`, TVP/list inputs, multiple simultaneous input properties, and unknown input kinds are rejected/deferred. In particular, the Task 1 Ticket B `parameterSetFile` spelling was not an existing operation shape; the generator now expresses its boundary-date input as `fixed` typed scalar data. This makes the fixture honest without claiming MCP fixed-file support. CLI fixed `--params-json` and MCP inline fixed parameters remain separate transport surfaces.

Paths are non-empty relative paths with no rooted, drive-qualified, URI, or parent traversal segments. Resolve from the manifest directory, normalize, and require the result to remain below the canonical input root. Reject reparse points/symlinks in any path component (including links that resolve back inside) as a conservative fail-closed rule. Then check the resolved target's size and exact raw-byte SHA-256. Do not follow link targets. The proposed manifest cap is 1 MiB; SQL/setup references are at most 16 MiB each; parameter files retain the 16 MiB per-file and 32 MiB aggregate raw ingress limits per operation, with current independent materialization limits unchanged. These are candidate study semantics, not product changes.

The evaluator first validates the complete manifest and every referenced input for every entry. Any preflight defect blocks all dispatch. After a successful all-suite preflight, it rechecks each entry's referenced hashes immediately before that entry's dispatch. If an input changes at that point, dispatch stops; already completed outcomes remain recorded. This is not an atomic snapshot: a same-user writer can still race a read, and a hash pins bytes, not a database snapshot or old artifact validity. No general suite resume, retry, or artifact reuse exists. Each run starts from the manifest; a partial prior `results.json` is neither read as an input nor a resume token.

Each dispatch is a call to the evaluator's injected in-memory mock dispatcher. It records a deterministic synthetic operation identity, ordered inputs/case labels, exact fixed values, profile/vars/subject/scope and policy fields. The injected outcome supplies execution, comparison, equivalence, performance, and coverage metadata. A completed `different`/`regression` result is preserved as completed negative evidence. An injected `error`, `timeout`, `cancelled`, or `unknown` completion is explicitly `comparisonStatus=not-run`, `equivalence=not-evaluated`, `performance=not-measured`, with zero completed cases; it stops subsequent dispatch and is not reported as a completed comparison. There is no retry. An unexecuted tail is marked `not-dispatched`, not failed, equivalent, or measured. No suite-level equivalence, winner, or aggregate success verdict is computed.

Changing target from a prior entry is permitted only when that entry explicitly names its profile and vars and the requested target is already within the operator's existing scope and policy. There is no inherited target or substitution. The simulator cannot prove operator scope or authorization. Mutation entries and any mutation-like operation are rejected before dispatch.

## Decision matrix

| Option | Meaning | Current study fit | Decision boundary |
|---|---|---|---|
| A. External command list/helper | Operator stores exact individual invocations outside SQLHarness. | Viable for shapes the selected surface can already express; plain CLI command expansion may require duplicated literals and cannot silently preserve named-set semantics. | Prefer when operators already keep reviewed commands and demand is not recurring. |
| B. Supported CLI manifest surface | A future CLI command validates a manifest and dispatches existing Core compare operations. | Not justified by this mock. CLI fixed compare and text matrix are expressible; MCP named sets are not CLI compare semantics. No CLI named-set flag may be invented in this study. | Defer until repeated demand and a frozen surface/input grammar are evidenced. A future implementation needs product-specific input-root, canonical path, TOCTOU and operation/artifact ID design. |
| C. No new feature | Continue individual compare calls and manual evidence organization. | Safe default; the synthetic placement estimate is not observed demand. Existing MCP named sets and compare operations remain usable individually. | Accept unless real recurring work shows command reconstruction costs that A does not address. |

The study deliberately makes no universal winner choice. It reports feasibility and limits for each option; synthetic outcomes cannot establish real demand, performance, authorization, or production equivalence.

## Case inventory

`cases.json` names each deterministic synthetic run. The verifier contains its own expected call counts/order and status assertions rather than trusting expected values supplied by the case data. Cases include successful and negative completions, stop outcomes and unexecuted tails, all-suite file/hash/manifest/input rejection, an after-preflight hash change, and a real symlink attempt with explicit simulated fallback if the OS denies link creation. No case can supply an executable command.
