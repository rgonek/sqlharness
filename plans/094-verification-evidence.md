# Plan 094 verification evidence

## Scope and implementation base

Plan 094 adds compact annotation receipts as the default response while keeping explicit full detail available. The implementation base recorded before Task 1 edits is `6cc848c63069171a7fcc8b3d3853530b70db5e2e`; Task 3 resumed on `43cae7eb` after reviewed Task 1 and Task 2 commits. The saved full implementation base remains the formatting comparison base.

The evidence below comes from served MCP fixtures with a temporary SQLite journal. It measures serialized UTF-8 input arguments and model-visible output bytes, not provider tokenization. No paid agent trial or live database was used.

## Task 1 RED and Task 2 GREEN

Task 1 first ran the requested focused suite against the old contract: 65 passed, 0 failed. The final served regression filter then failed meaningfully with 9 failed and 66 passed (75 total), because both served schemas lacked `responseDetail` and `maxResultBytes`, old responses echoed labels, and the required minimum-budget full-detail error guidance was absent. The complete output remains in `.superpowers/sdd/094-compact-annotation-receipts/task-1-red-output.txt`; the reviewed correction output remains in `task-1-review-fix-round-1-output.txt`.

Task 2's implementation at `43cae7e` passed its combined annotation/schema/wiring/served tests (77/77) and token-budget tests (45/45). Task 1 and Task 2 reviews were approved with no open findings. Their detailed reports and review rulings remain in the SDD work folder.

## Task 3 served workflow measurement

The served fixture submitted the same twenty successful label/keep updates (120 `界` code points per label) against a 4096-byte process ceiling. The legacy path first requested full detail and received the actual oversized-response error, then resubmitted seven full-detail chunks of up to three items. The receipt path sent the batch once with the default detail.

| Workflow | Calls | Serialized input arguments | Model-visible output | Result |
|---|---:|---:|---:|---|
| Full echo, rejected request, then split resubmission | 8 | 32,080 B | 17,279 B | 20 updates persisted across seven successful split calls |
| Default compact receipt | 1 | 15,911 B | 2,069 B | All 20 updates persisted in one call |

The old workflow output includes the actual rejected response text: `The complete annotation response exceeds the effective 4096-byte result budget (maximum allowed 4096 bytes). use responseDetail 'receipt'. No annotations were changed.` Each legacy input includes `responseDetail: "full"`; receipt mode uses the omitted default. Outputs are measured from the returned MCP text block. The repeated fixture run passed and printed these values from the served response path.

This is a deterministic workflow fixture, not a measurement of model tokens or provider-side retries. It records the caller's necessary resubmission explicitly; it does not include discovery or chat-history bytes.

## Catalog and off-mode parity

The baseline checkout at the recorded pre-change revision was used to run the existing served annotation-gating test and query-result byte fixture. The exact serialized feature-off `tools/list` and representative query result were hashed, then frozen in Task 3 regression assertions:

| Served surface | Pre-change baseline | Receipt branch | Exact parity |
|---|---:|---:|---|
| Fixed SQL Server feature-off `tools/list` | 28,248 B; SHA-256 `C6EC82792E9BE736FD8E87E3FB401F6CA5C473E0502F889E0FA2777DFBF11AA9` | 28,248 B; same SHA-256 | Yes |
| Representative feature-off query result text | 285 B; SHA-256 `611BF72927E6ADD8399D4F776D7B9D094FA4ED779CA8BFA59233802B89E085C1` | 285 B; same SHA-256 | Yes |

The enabled fixed SQL Server catalog is 31,856 B of the 32,768 B limit. Request-scope enabled catalog remains within the limit as covered by schema tests. The serialized catalog bytes include the current served schema and descriptions. The 085 evidence's prior feature-off/on sizes are historical measurements; they do not represent receipt responses.

## Task 3 focused verification

Command:

```powershell
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --filter "FullyQualifiedName~AnnotateToolTests|FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpOperationAnnotationWiringTests|FullyQualifiedName~ServedAnnotationReceiptTests|FullyQualifiedName~McpTokenBudgetTests|FullyQualifiedName~McpJournalTests.Invalid_annotation_request_is_journaled_without_target_or_reference" --logger "console;verbosity=detailed"
```

Result: exit 0, 123 passed, 0 failed. Output included both workflow byte/call counts, off-mode golden hashes and sizes, and the enabled catalog size. This command was run after the exact-hash assertions and complete input accounting were added.

## Common gate and review status

Task 3 implementation review and whole-branch review are pending. Direct branch formatting and the final Windows `just verify` gate will be recorded after the Task 3 commit. No merge or push is authorized. Linux verification, live-database behavior, and actual-agent token costs are unverified and outside this acceptance evidence.
