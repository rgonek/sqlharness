# Plan 105 interface repair report

## Review finding

The scoped review of `6d3f7f1c` approved the original two Important findings and workflow Minor, and identified a new Important interface regression: the final fix wave reduced artifact guidance to vague labels to fit the existing tools/list ceiling. This repair restores the missing caller contract in shared descriptions used by fixed and request scopes.

## Result

Artifact descriptions now tell callers that cursors are zero-based and apply to `matrix-cells`, `operators`, `statements`, and `plan`; to follow the returned continuation; and that executable selectors apply only to plan reads. They name `query` for measure/set plans, `baseline` or `candidate` for compare, the zero-based statement ordinal, exact measure parameter set and exact repetition (defaulting to the last measured repetition).

The page-size help includes the 512 maximum, defaults of 32 matrix cells, 32 operators, 128 statements and 128 plan nodes, and explains that the byte budget may return fewer items so callers should continue. Metrics help retains `planDocumentOrdinal`/`planQueryOrdinal` identity and STATISTICS IO `ScanCount`; statement help distinguishes object logical reads and Showplan `ActualScans`, reports unavailable/partial rather than zero, and points callers to the statements continuation for exact sources.

To make room without reducing catalog ceilings or removing prior safeguards, the duplicated optional-diagnostics paragraph in Measure and Compare was shortened. It still marks retrieval optional, identifies `sqlharness_artifact` with `metrics`/`statements`, and retains the exact repetition/row-limit pointer. Existing measurement, comparison verdict, equivalence, dry-run, and setup semantics remain in those descriptions.

## Tests

The schema semantics test now checks these contracts in both catalogs. RED was observed before restoration: it failed because the exact source continuation guidance was absent, while request-scope tools/list was 32,786 UTF-8 bytes against a 32,768-byte ceiling.

The final focused command passed 2/2:

```text
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "FullyQualifiedName~Tools_list_explains_optimization_evidence_semantics_in_both_modes|FullyQualifiedName~Operation_annotation_members_are_gated_by_the_startup_config" --logger "console;verbosity=detailed"
```

The exact serialized tools/list sizes were 28,662 bytes with annotations disabled, 32,270 bytes for annotations-enabled fixed scope, and 32,713 bytes for annotations-enabled request scope. Both enabled catalogs remain below 32,768 bytes.

`git diff --check` passed. This repair did not run the full Windows gate or formatter; those remain with the controller. No live database, UI, merge, or push was involved.
