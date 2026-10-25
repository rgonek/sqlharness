### Spec Compliance

- ❌ Issues found: `plans/118-evidence/inventory.md:25-39` is not yet a complete table of retained fields. It gives no row for retained `execution.operation` or `execution.warmup`; it mentions session policy and setup binding only in prose, without their sensitivity/omission and comparison-use dispositions. The table also limits its parameter row to scalar parameters, while the builder retains materialized list/TVP rows, values, and source text (`ConditionsModelBuilder.cs:80-100`). Add rows or an explicit grouped disposition for these retained fields.
- ❌ Issues found: `plans/118-evidence/inventory.md:9-21` quotes the `ConditionsExecution` construction and the model record signature, but not the fingerprint payload builders requested by the brief. The claims about included and excluded fingerprint fields need short excerpts from the exact builders at `ConditionsModelBuilder.cs:124-191`.
- ❌ Issues found: `plans/118-evidence/inventory.md:29,63` says the “(value not stored)” label indicates evidence limits rather than NULL and that typed NULL and absent inputs remain distinct, but it omits a current display collapse: `ToCondition` materializes typed null as JSON null (`ConditionsModelBuilder.cs:195-197`), and `ConditionsList.parameterValue` renders both null and undefined as “(value not stored)” (`ConditionsList.tsx:4`). Qualify the claim and record this display boundary so the label is not treated as proof that the measurement value was absent.

### Strengths

- The inventory distinguishes regular and measurement fingerprints, including comparison mode and the per-case varying-parameter pairing (`plans/118-evidence/inventory.md:13,30-32,36`); the focused source check confirmed the cited builders.
- It describes “conditions differ” as a fail-closed generic state and records the current shared UI/Markdown wording (`plans/118-evidence/inventory.md:44,47`); the focused source check confirmed those branches and copy.
- The six required topics are present in the inventory, and the scope statement avoids proposing capture, retention, or comparison-policy changes (`plans/118-evidence/inventory.md:30,33-39,63`).

### Issues

#### Critical (Must Fix)

- None.

#### Important (Should Fix)

- `plans/118-evidence/inventory.md:25-39` — The retained-field table omits several retained fields and structured-parameter detail. This leaves the study incomplete for later explanation design. Add their fingerprint, sensitivity/omission, and comparison dispositions.
- `plans/118-evidence/inventory.md:9-21` — The required excerpts do not show the fingerprint payload builders themselves, so readers cannot verify the central include/exclude claims from the copied code. Add short excerpts from the cited builder methods.
- `plans/118-evidence/inventory.md:29,63` — The current conditions detail UI renders typed JSON null and missing values with the same “(value not stored)” label. Record this ambiguity; otherwise the study could lead a later explanation to infer absence where the stored measurement was NULL.

#### Minor (Nice to Have)

- None.

### Assessment

**Task quality:** Needs fixes

**Reasoning:** The inventory is well structured and its matching/generic-message analysis is source-grounded, but it misses required retained-field coverage and payload-builder excerpts. Its parameter-value statement also needs to account for the existing typed-null display collapse under the global distinction constraint.
