### Spec Compliance

- ❌ Issues found: The independent oracle does not cover the constrained `set` mode or partial diagnostics alongside an unchanged equivalent verdict; it also does not exercise the 16-item output cap or overflow beyond 256 columns per described set. These behaviors/limits are required for evaluation by the global constraints (`constraints.md:18-20`) but are absent from the cases array (`plans/117-evidence/cases.json:12`).
- ⚠️ Cannot verify from diff: The brief requires reopening source references. The inventory cites the relevant reader, comparer, artifact, and projection locations, and the report claims they were reopened, but that action and citation accuracy are not observable in this snapshot diff.

### Strengths

- `inventory.md:7-35` clearly follows the evidence flow through capture, comparison, artifacts, and agent projection, and explains where schema details, set boundaries, and provenance are lost.
- The fixture set gives explicit case/artifact identities and repetitions, intentionally reuses one SQL hash, and covers ordered locations, schema changes, nullability, instability, legacy artifacts, row/fingerprint limits, metadata limits, and Unicode names.
- Expectations preserve the existing verdict in the covered partial and unavailable cases; the multiset example explicitly avoids inventing a cell or position from row fingerprints.

### Issues

#### Critical (Must Fix)

None.

#### Important (Should Fix)

- `plans/117-evidence/cases.json:12`; `constraints.md:3,18-20`: Add independent cases for set-mode duplicate-insensitive behavior and a partial diagnostic whose existing comparison verdict is still equivalent. The current multiset-only duplicate case and false-verdict partial cap cases cannot establish these constrained semantics.
- `plans/117-evidence/cases.json:12`; `inventory.md:43,58`: The fixtures do not exceed the 16-item cap or the 256-column-per-set cap. Case 021 has exactly 256 columns and exceeds the metadata-byte budget, so it does not isolate the column cap. Add overflow cases with known omitted counts and unchanged authoritative verdicts.

#### Minor (Nice to Have)

None.

### Assessment

**Task quality:** Needs fixes

**Reasoning:** The evidence inventory and most oracle cases are clear and well-scoped, but the oracle leaves several explicitly constrained decision branches unmeasured. Add those cases before treating Task 1 as a complete independent baseline.
