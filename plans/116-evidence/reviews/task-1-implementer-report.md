# Plan 116 Task 1 report

## Status

DONE

## Work performed

Created `plans/116-evidence/inventory.md` with the capability/runtime identity boundary, current MCP/Core/CLI/Journal/dashboard/artifact integration paths, schema version homes, and the two incident evidence limits. It distinguishes current capability data from what persisted evidence could establish later and preserves the explicit constraints: `configurationRef` remains configuration identity, artifact ownership remains authorization, no default absolute executable paths/environment dumps/executable hashes, no new masking policy, and no budget increase.

Study start was commit `443f468e620e98c2e65fb109040d91ef06c286c5`, with an initially clean worktree. The scoped source drift scan from `5f7342a7f5d48e03268491059949d1edfc24c65c` produced no output. No source/test changes, staging or commits were made.

## Focused verification

- Required topic check: `Select-String -Path plans/116-evidence/inventory.md -Pattern '107|113|McpCapabilitiesDocument|SessionIdentity|ArtifactManifest'` — all five topics present with source references.
- Manually reopened the cited code/document/test excerpts while composing the inventory; focused source excerpts were checked for MCP capability construction, host session/client identity, journal schema/upsert, manifest version and producer ref, CLI assembly metadata, protocol/version docs, test paths, dashboard linkage, and both incidents.
- Self-review: reviewed the new-file patch with `git diff --no-index -- NUL plans/116-evidence/inventory.md` and reran `git diff --check` after edits; only the requested inventory is a tracked worktree change. This is a notes-only study; no executable tests or product gate were run.

## Concerns

No blocker. Capability metadata does not currently identify the loaded SQLHarness MCP managed assembly. Existing protocol/client/session and artifact producer references are partial attribution evidence only; they do not resolve historical loaded-build identity. Historical causation remains unresolved in both cited incident records.

## Report path

`.superpowers/sdd/116-loaded-producer-identity-study/task-1-report.md`

## Round 1 fixes

Corrected the artifact-owner documentation reference to `docs/mcp/startup.md:86`, extended the `McpHost` reference through the protocol-version assignment at line 160, moved the MCP test references to `tests/SqlHarness.Mcp.Tests`, corrected the quoted comment to `McpOperationMapper.cs:29-31`, and changed the dashboard wording to workflow-cost grouping and producer-reference display. Reopened each corrected excerpt and test range.

Required topic check command:

```powershell
Select-String -Path plans/116-evidence/inventory.md -Pattern '107|113|McpCapabilitiesDocument|SessionIdentity|ArtifactManifest'
```

Output: matches at inventory lines 11, 17, 18, 19, 30, 31, 51, and 55; all five required topics present.

Required diff check command:

```powershell
git diff --check
```

Output: no output; exit 0.
