# Plan 105 artifact interface repair

## Contract repaired

The final scoped review found that compact tool descriptions had removed required caller guidance. Fixed-scope and request-scope catalogs now share concise artifact argument descriptions covering:

- zero-based cursors for `matrix-cells`, `operators`, `statements`, and `plan`, with returned continuation;
- executable `plan` selectors: measure/set variant `query`, compare variants `baseline`/`candidate`, zero-based statement ordinal, exact measure parameter-set name, and exact repetition (default last measured);
- page-size ceiling 512 and default windows of 32 matrix cells, 32 operators, 128 statements, and 128 plan nodes; the byte budget may return fewer, so continue;
- metrics grants/compile identity by `planDocumentOrdinal`/`planQueryOrdinal`, STATISTICS IO `ScanCount`, and statement `planDiagnostics`/object logical reads with Showplan `ActualScans` distinguished; missing evidence is unavailable/partial, never zero; use statements continuation for exact source identities.

Measure and compare retain their prior workflow safeguards while compressing the duplicated optional-diagnostics paragraph. They still say the read is optional, identify `sqlharness_artifact` and `metrics`/`statements`, and retain the exact repetition/row-limit pointer.

## RED/GREEN evidence

New assertions in `Tools_list_explains_optimization_evidence_semantics_in_both_modes` exercise the descriptions served by both fixed and request scopes. The first RED run reproduced the regression: the tool description lacked the exact-source retrieval pointer, and request-scope tools/list measured 32,786 bytes, 18 bytes above its 32,768-byte ceiling.

Final focused command:

```text
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "FullyQualifiedName~Tools_list_explains_optimization_evidence_semantics_in_both_modes|FullyQualifiedName~Operation_annotation_members_are_gated_by_the_startup_config" --logger "console;verbosity=detailed"
```

Result: 2 passed, 0 failed. Measured catalogs:

- annotations off: 28,662 UTF-8 bytes;
- annotations on, fixed scope: 32,270 / 32,768 bytes;
- annotations on, request scope: 32,713 / 32,768 bytes.

`git diff --check` passed after the C# no-final-newline convention was restored.

## Verification boundary

This is a bounded interface-description and schema-test repair. No Core behavior, output ceilings, UI, live database, full gate, format gate, merge, or push was changed or exercised. The controller owns the next independent scoped review and final Windows gate.
