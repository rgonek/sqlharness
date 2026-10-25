### Findings

- **Required-version null optional fields — ADDRESSED.** `evaluate.ps1:40` and `evaluate.ps1:42` clear informational version and MVID when that assembly's required version is missing; the Core projection uses those normalized values (`evaluate.ps1:62`, `evaluate.ps1:63`). The verifier asserts both are null in the unavailable subcase (`verify.ps1:129`, `verify.ps1:130`).
- **Absent versus null `artifactProducer` — ADDRESSED.** The evaluator checks whether the fixture contains the property before assigning applicability (`evaluate.ps1:133`, `evaluate.ps1:153`, `evaluate.ps1:154`). The verifier checks the independently derived artifact state and reason (`verify.ps1:114`, `verify.ps1:115`, `verify.ps1:116`, `verify.ps1:117`). The results diff restores `not_applicable` for cases without artifact provenance while retaining `unavailable` for the explicit null old-artifact case.

### New Breakage

None found in this fix diff.

### Spec Compliance

- ✅ Both scoped findings are addressed.
- ⚠️ The existing compact, pageSize-driven paging fixture and non-wire budget estimates remain deferred limitations, as stated in the report.

### Code Quality

**Task quality:** Approved

**Reasoning:** The unavailable assembly projection now nulls optional facts alongside its missing required version, and the verifier asserts the shape. Artifact applicability now distinguishes an omitted field from an explicitly missing producer. The appended report records successful focused runs; I did not rerun them.
