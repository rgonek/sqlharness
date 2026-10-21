# Plan 095 Task 1 Review

### Spec Compliance

- ❌ Issues found: The brief requires unknown and foreign artifact refusals to remain content-free under selectors and a larger budget. The new test only excludes the artifact ID, one selector, and one operator-name canary ([McpToolSchemaTests.cs:683] and [McpToolSchemaTests.cs:689]); it can pass if statement hashes, ordinals, or other saved rows leak. Assert that the refusal contains no saved section payload, including distinctive synthetic hashes or record fields.
- ✅ The other Task 1 requirements are represented: fixed/request schemas expose budget and page-size bounds; the served test compares the 64 KiB default with a 128 KiB request at a 256 KiB process ceiling; page sizes 1, omitted/default, 512, 0, and 513 plus a non-pageable section are exercised; reader fixtures reconstruct 513 plan nodes and 65 matrix entries under 4 KiB and 64 KiB budgets.
- ⚠️ Cannot verify from this tests-only diff: production behavior from Fix contract items 1–7 is intentionally deferred to Task 2. In particular, selected run/repetition, omission counts, no-node-fit truthfulness, all final-envelope budget paths, capabilities/catalog limits, and the no-DB/no-persistence constraints need checking against Task 2 and the whole-task review.

### Strengths

The new tests exercise the actual served catalog, ArtifactReader, and AgentOutputProjection with synthetic saved artifacts. They measure served UTF-8 response bytes and verify ordered reconstruction without duplicates.

The supplied red logs show both focused test projects compiled and reached the intended failures: MCP 7 failed / 99 passed, Core 7 failed / 62 passed. I read the saved logs and did not see compiler warnings or unrelated test failures.

The Task 1 diff stays test/evidence-only, as requested for a tests-first task, and the report records the intended RED and useful Task 2 integration constraints.

### Issues

#### Critical (Must Fix)

None.

#### Important (Should Fix)

- [McpToolSchemaTests.cs:683] and [McpToolSchemaTests.cs:689] — The refusal test does not establish that the response is content-free; it checks only three canaries. A regression could disclose statement hashes, ordinals, or other saved records while passing. Assert that no saved section/result payload is present and check distinctive record values from the fixture.

#### Minor (Nice to Have)

- [McpToolSchemaTests.cs:600], [ArtifactReaderTests.cs:668], and [ArtifactReaderTests.cs:703] — The continuation reconstruction loops have no progress or iteration guard. If a broken continuation repeats a cursor, these tests can hang instead of failing. Assert that each next cursor advances and/or cap iterations by the number of source items.

### Assessment

**Task quality:** Needs fixes

**Reasoning:** The tests compile and provide meaningful RED for the requested budget and source-window behavior. Strengthen the explicit content-free refusal regression and guard the reconstruction loops before treating this task as approved.

**Review execution note:** I initially ran `git status` and `git diff 384357c..add7efa` before switching to the supplied diff artifact, contrary to the template. Both commands were read-only; I did not rerun tests or alter the worktree, index, or HEAD.
