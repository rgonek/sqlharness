# Plan 107 Task 3: bounded diagnosis and follow-up

Recorded 2026-10-23 on branch `plan-107`. This closes the diagnostic scope; it does not change product behavior or establish which build handled the historical requests.

## Finding

The known-source offline fixture confirms the rejection symptom on the inspected source path. The MCP real-SDK characterization ran 12 cases: ten ordinary requests (singleton, multi-value, empty list, multiple named sets, and setup reuse, each for `IN @Ids` and `IN (@Ids)`) and two cases with an explicitly supplied `@Ids1`. All 12 returned `safety_rejected`. Ordinary bare-form requests reported `ParseError` in setup or baseline validation; ordinary parenthesized requests reported a generated `@Ids`/`@Ids1` collision. Both supplied-collision cases also rejected, preserving the expected caller-collision outcome. The Core characterization suite passed 65/65; the MCP suite passed 5/5 and includes the 12-request matrix. The fixture asserted zero connection attempts.

The test host logged its loaded `SqlHarness.Mcp` and `SqlHarness.Core` informational versions, file locations and MVIDs in [the MCP characterization log](task-2-mcp-tests.log). The Core suite result is in [the Core test log](task-2-core-tests.log). Separate [Core](task-2-core-red-contract.log) and [MCP](task-2-mcp-red-contract.log) RED-contract logs preserve the expected-success failures. Sanitized synthetic request objects and their case names are in [the request-shape file](task-2-request-shapes.json); they contain no private target names.

The historical PATH candidate is an older build than the inspected source, but that does not identify the binary mapped by the historical MCP processes. The historical process inventory did not establish loaded managed assembly identity. Therefore stale or mismatched deployment remains plausible, not proven. A client-payload difference is also unproven because the retained replay uses the synthetic request shapes. The evidence confirms a current-source failing compare path, while the exact mechanism behind each validation rejection remains unresolved. No product fix is claimed.

## Follow-up ownership

The source-side failures are assigned to the narrow implementation follow-up in [107-list-runtime-source-follow-up.md](107-list-runtime-source-follow-up.md): correct compare list classification with focused offline Core/MCP coverage while retaining the explicit caller-collision rejection. That implementation is outside this diagnostic task and requires its own authorization and review.

The deployment attribution check remains assigned to the operator or an operator-authorized runtime investigator: identify the exact managed build loaded by the affected MCP process (including assembly identity and file location), then run the same 12 synthetic dry-run requests against that exact process/build with zero database connections. Do not infer identity from the PATH executable, install or replace binaries, or restart a server to obtain it. Until this check is available, historical runtime cause and deployment attribution remain unresolved.

## Review-focus assessment

1. **Older artifacts and absent engine-specific fields:** not applicable; this diagnostic does not read or modify persisted artifacts or engine-specific fields.
2. **Variant, statement, set and repetition identities:** assessed in the offline matrix; each request records its case, SQL form and named-set shape. The matrix uses one repetition and does not claim repeated-run identity coverage.
3. **Byte budgets, escaped/multibyte text and continuations:** not applicable; no bounded artifact projection or clipping behavior changed or was evaluated.
4. **Errors and negative results:** preserved as explicit `safety_rejected` outcomes. The expected-success RED logs remain separate from green characterization logs; failures are not interpreted as successful empty checks.
5. **Offline evidence versus live acceptance/deployment/token savings:** the results are synthetic and offline, with zero connection attempts. They establish neither live acceptance, deployed-build identity, nor model-token savings.

## Scope and verification

Inspected source and fixture: `CompareCellRunner`, `SqlSafety`, `ListParameterExpander`, `McpOperationMapper`, and the existing Compare/ListParameterExpander/MCP parameter batch tests. Expected outcome for the ten ordinary shapes was successful dry-run; observed outcome was validation rejection in all ten. Expected outcome for the two caller-supplied `@Ids1` cases was rejection; observed outcome matched.

Focused task evidence: Core 65/65 passed and MCP 5/5 passed, with the MCP matrix reporting 12 requests and zero connections. Expected-contract RED runs are retained separately and are not green-test claims.

Final Windows gate: `just verify -LogDirectory plans/107-evidence/windows-gate-20261023-task3` exited 0 on tested revision `19035a07f16f04e77977ab5f1f15ecc01090ca21`. The five stages took 25m15s total: UI install passed; build passed with 0 warnings/errors; UI check passed with 64 files and 372 tests; Core passed 4,657 with 11 skipped (4,668 total); MCP passed 508 with 4 skipped (512 total); formatting was skipped because there were 0 changed C# files. The UI check log includes Node `DEP0190` deprecation output. All five nonempty stage logs are archived under [the Windows gate log directory](windows-gate-20261023-task3/). The branch formatter command `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef 6d543e50` completed successfully before the gate. Task 3 changed documentation and evidence only; the executable tree was unchanged from task-start `HEAD` `3493b16114c39d5b031b4490930504562044d38c`.

Documentation checks for this closeout: `git diff --check`, changed Markdown relative-link resolution, and review-focus assessment. Linux and live acceptance remain unverified.
