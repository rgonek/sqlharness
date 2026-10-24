# Plan 105 Task 2 failure evidence

Status: NEEDS_CONTEXT; no commit. Production fix attempts stopped after two projection changes, per controller instruction.

The new Core regression passed, and the focused Core suite passed (218 passed, 0 failed). The MCP workflow focused test remains failing only for `budget: 4096`; its 65536 variant passes. Fresh-build command:

```powershell
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "FullyQualifiedName~Compact_compare_keeps_partial_statement_case_and_artifact_reference" --logger "console;verbosity=normal"
```

This invocation did not use `--no-build`; output showed Core, MCP, WorkflowHost, CLI, and MCP tests built to their `bin/Debug/net10.0` locations. It reported 2 total, 1 passed and 1 failed. At `tests/SqlHarness.Mcp.Tests/AgentOptimizationWorkflowTests.cs:190`, the 4096-byte statement artifact fetch returned `{"schemaVersion":3,"status":"error","result":null,"error":{"code":"internal_error","phase":"internal","message":""},"truncation":{"omittedItems":1,"maxCellChars":0}}`.

Two speculative changes to `AgentOutputProjection` tried stricter accounting/truncation for object arrays and nested diagnostics/IO. Neither fixed the failure. Earlier exception-type instrumentation is inconclusive: the observed test/workflow-host binaries were older than the instrumented source when checked; prior command flags were not retained. A fresh normal build still fails, so stale binaries are not the current cause. The exact failing catch or SDK boundary remains unknown. The normal code catches use the nonblank `InternalMessage`; the adapter's explicit size fallback reports `output_budget_too_small`, neither of which matches this response.

No further production fix was attempted. Keep the working tree uncommitted until the MCP failure is traced. Batch-only diagnostics reachability and omission accounting (especially PlanDiagnosticCases) still need review.
