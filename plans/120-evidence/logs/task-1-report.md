# Task 1 report — demand assumptions and existing alternatives

## What changed

- Added `plans/120-evidence/inventory.md`, mapping each proposed manifest field to its current CLI/Core/MCP contract and authoritative source for limits. It explicitly records unsupported combinations, rooted-path and hash limitations, and the proposed-versus-existing bounds distinction.
- Added `plans/120-evidence/fixtures/generate.ps1` and two synthetic ticket workspaces. Ticket A contains two SQL subjects and three typed scalar cases (small, large, empty). Ticket B contains three SQL revision pairs, setup input, the 2024-02-29 boundary date, duplicate-sensitive mock rows, and one candidate file deliberately changed after its manifest hash was pinned.
- Added exact generated SHA-256 records and per-file manifest pins. Manifest references are relative to each manifest directory. Ticket B's single stale pin is intentional; its pinned and current digests are both recorded exactly.
- Added a transparent reconstruction-placement estimate (60 plain command-list placements versus 26 proposed manifest placements) and labeled it a synthetic ergonomics estimate, not observed user time, model-token use, or evidence of recurring demand.

## Focused checks and results

- `pwsh -NoProfile -File plans/120-evidence/fixtures/generate.ps1` — exit 0; generated 21 files recorded in `SHA256SUMS.txt`. Repeated the command and compared checksum-file contents with `Compare-Object`; no differences, so generation was byte-stable in this environment.
- `Select-String -Path plans/120-evidence/inventory.md -Pattern 'CLI|MCP|Core|named|matrix|bounds'` — 23 matching lines; all required topics are represented. Reopened the cited Core compare/parameter/target contracts, CLI command registrations and option definitions, MCP argument schemas/descriptions and mapping, input-root documentation, operation/file bounds, and the SQLHarness user-skill contract while preparing the inventory.
- PowerShell integrity check parsed both manifests and all Ticket A case files, checked all 21 checksum records, resolved every reference relative to its manifest directory, and verified every pinned digest except the one expected stale `changed-revision` candidate. Result: `Ticket A/B JSON and SHA-256 integrity: PASS (one deliberate stale pin); generated checksums: 21`.
- `git diff --cached --check` — passed after staging. Added a scoped `.gitattributes` rule so Git preserves the LF bytes that the fixture SHA-256 values pin across checkouts.

No product command, SQLHarness process, MCP tool/client, target, database, or shell command embedded in fixture data was run. No product test suite was run because this change contains only research documents and synthetic inputs.

## Self-review

- The proposed 16-entry suite cap and 1 MiB manifest cap are labeled as study choices; current parameter-set, matrix, SQL, timeout, repeat, raw-file and materialization bounds remain separately attributed to current sources.
- CLI compare's matrix support is not conflated with MCP named-set or set-file input. The Ticket A shape is identified as unsupported by one equivalent CLI compare request; the empty text case is not falsely represented as CLI matrix syntax.
- Manifest hashes pin bytes but do not establish database snapshot identity, execution permission, artifact validity or permission to reuse evidence. The deliberate mismatch has a stop-before-dispatch mock outcome and is not counted as an execution failure or comparison result.
- Synthetic names and SQL are isolated under the evidence folder. Profiles are fictitious labels, no credential values are included, and output contains no invented user-interview or real-demand claim.

## Concerns and limitations

- Recurring multi-subject demand remains unconfirmed; the ergonomics counts are constructed examples and do not establish that a supported runner is justified.
- The proposed relative-path resolver still needs a fail-closed design for symlinks/reparse points and MCP input-root enforcement. Existing MCP file handling does not provide a caller-supplied manifest hash precondition.
- The 26-versus-60 placement model is not semantic parity: the plain CLI expansion is a manual reconstruction estimate and does not preserve the MCP named-set operation shape for the empty text case.

## Review round 1 — duplicate-sensitive Ticket B case

Finding: the previous `duplicate-counts` fixture differed in row values as well as multiplicity, so deduplicating both outcomes still showed a difference; its grouped SQL also could not emit repeated rows.

Changed the pair-02 baseline query to return source rows and the candidate query to use `SELECT DISTINCT`, with the same filter and selected row shape. The mock outcome now has baseline `[[4,3],[4,3]]` and candidate `[[4,3]]`. Updated the generator to preserve nested one-row arrays during canonical JSON generation, then regenerated the manifest digest and `SHA256SUMS.txt`.

Checks and actual results:

- `pwsh -NoProfile -File plans/120-evidence/fixtures/generate.ps1` run twice — both runs reported `Generated 21 fixture files`; the pinned Ticket B mismatch remained `8949ef817baf41e8b4b46528ba41e6ed7ea6e3790f1b3aff9e9cb1dbdbaa7f05` versus current `6396ff03f0fa635d123aa4301ccd88c77dcbd0beb95131ff2aaeb7f5ca55d8d7`; checksum comparison was empty and passed.
- Independent PowerShell multiplicity assertion loaded `ticket-b/mock-outcomes.json`, compared the distinct row keys, checked counts 2 versus 1, and checked baseline SQL selects all rows while candidate SQL uses `DISTINCT` with the same `@includeHidden` filter. Output: `Multiplicity assertion: PASS (same distinct row [4,3]; baseline count 2; candidate count 1; SQL selects all rows vs DISTINCT)`.
- The first assertion attempt found that the JSON generator had flattened the candidate's single nested row into `[4,3]`. The generator now builds nested arrays explicitly; the corrected assertion above passed.
- Regeneration and SHA-256 check over all fixture records and every Ticket B manifest reference — output: `Regeneration and SHA-256 checks: PASS (21 checksums; 11 Ticket B references; one deliberate stale pin)`.
- `git diff --cached --check` — passed after staging, before this fix commit.
