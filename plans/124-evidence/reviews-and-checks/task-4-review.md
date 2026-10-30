### Spec Compliance

- ✅ Scoped requirements are met by the reviewed changes under the controller's Windows-only Save ruling. The target-change test checks rejection before any session or commands, and inherited policy and startup snapshot have focused tests (`tests/SqlHarness.Tests/Dashboard/MutationExecutorTests.cs:61`, `tests/SqlHarness.Tests/Dashboard/MutationExecutorTests.cs:184`, `tests/SqlHarness.Mcp.Tests/McpMappingTests.cs:172`). Documentation distinguishes dashboard rechecks from MCP startup snapshots (`README.md:604`, `docs/mcp/startup.md:62`).
- ⚠️ The target test calls the executor with a prepared request and approval context; it does not exercise the broker's actual pending/approval lifecycle. The MCP test checks the capabilities projection after profile replacement, not a running transport or a changed global configuration. Installed/live activation and non-Windows runtime behavior remain unverified.

### Strengths

- The target identity test exercises preparation through Core and verifies both no target session and no commands (`tests/SqlHarness.Tests/Dashboard/MutationExecutorTests.cs:184`).
- The evidence explicitly records the gate revision, later EOF-only correction, skips, lack of RED evidence and platform limits (`plans/124-evidence/verification.md:3`, `plans/124-evidence/verification.md:5`, `plans/124-evidence/verification.md:13`).

### Issues

#### Critical (Must Fix)

- None found.

#### Important (Should Fix)

- None found within the scoped changes.

#### Minor (Nice to Have)

- `plans/124-evidence/browser-fixture.mjs:16`: the success scenario checks only the number of submitted requests after a fixed wait; it does not assert successful completion or the restart/reconnect notice. Its PASS therefore demonstrates submission, not the successful save message. Add assertions for the success state and activation notice when the fixture is next extended.
- `plans/124-evidence/verification.md:15`: the gate has two pre-existing React hook warnings and Node DEP0190. They are disclosed and the affected source is reported unchanged; they make the output non-pristine but are not attributed to this task. The expected conflict also emits a console error (`plans/124-evidence/logs/browser-final.log:4`).
- `plans/124-evidence/reviews-and-checks/ledger.md:19`: the initial all-pending snapshot remains alongside subsequent completion entries, which can confuse readers; label it as the initial state.

### Assessment

**Task quality:** Approved.

**Reasoning:** Scoped safety recheck tests and activation documentation are consistent with the accepted design. Browser success-state assertion and end-to-end broker/transport coverage remain limited; the supplied gate and browser evidence were reviewed without rerunning tests. Dirty-draft refresh behavior is reserved for whole-branch review as directed.
