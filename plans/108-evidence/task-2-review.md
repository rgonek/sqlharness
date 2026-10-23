### Spec Compliance

- ✅ Spec compliant. `ArtifactReader` reports accepted variants by artifact kind while keeping `Safety`; the measure-only default remains gated by `MeasureKind`/`MeasureSetKind` at `src/SqlHarness.Core/ArtifactReader.cs:459`, and compare still requires `baseline` or `candidate` at `src/SqlHarness.Core/ArtifactReader.cs:463`.
- ⚠️ Cannot verify from this diff: Task 3 owns offline MCP request-level omission coverage and the full gate; the controller should confirm both there.

### Strengths

- `ReadSectionWithMetadata` checks scope ownership at `src/SqlHarness.Core/ArtifactReader.cs:229` before dispatching the plan section at `src/SqlHarness.Core/ArtifactReader.cs:246`; variant defaulting and validation happen within `ReadPlan` afterward. The new wrong-owner assertion covers this ordering at `tests/SqlHarness.Tests/ArtifactReaderTests.cs:167`.
- Both served tool descriptions state the measure default and compare requirement (`src/SqlHarness.Mcp/Tools/McpRequestToolHandlers.cs:194`, `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:388`), and `docs/mcp/artifacts.md:11` documents defaults, required values, and rejected variants.
- The invalid-variant cases cover measure, measure-set, and compare and retain the `Safety` assertion (`tests/SqlHarness.Tests/ArtifactReaderTests.cs:145`). The reported focused suite is accepted as implementer evidence; I did not rerun it per reviewer instructions.

### Issues

#### Critical (Must Fix)

- None.

#### Important (Should Fix)

- None.

#### Minor (Nice to Have)

- `tests/SqlHarness.Tests/ArtifactReaderTests.cs:175` checks only the accepted-values substring. Since measure and measure-set both accept `query`, this assertion would not catch those two diagnostics accidentally naming the same artifact kind. Also assert the kind-specific wording (`measure` versus `measure-set`) to protect that distinction.

### Assessment

**Task quality:** Approved

**Reasoning:** The implementation follows the requested validation order, preserves compare's explicit variant requirement, and updates both served descriptions and the artifact docs. The only issue is a small test assertion gap; Task 3 remains responsible for request-level MCP coverage and the full gate.
