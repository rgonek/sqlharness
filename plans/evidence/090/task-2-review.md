### Spec Compliance

- ✅ Spec compliant: Task 2 records the product revision, deterministic live-set counts, actual staging ownership and expiry, remaining limits, explicit branch formatter result, and Windows gate counts. The updated contract correctly distinguishes Codex/OpenCode in-flight batch membership from the Claude-only persistent staging writer, and makes no process-memory or performance claim. References: `plans/090-verification-evidence.md:3-14,17-25`; `plans/090-matcher-title-backfill-retention.md:11-20,23-35,52-54`; `plans/README.md:18`; `plans/history.md:3-5`.
- ⚠️ Cannot verify from the retained diff/logs alone that the Windows gate ran against product SHA `1e6b0eb03330099d69495d4e59d478498ca27097`: the evidence document names that SHA, while `gate-console.log:1-7` confirms the gate command, stages, duration, and exit code but does not record a source revision. The controller can confirm the association from the original run metadata if available; no gate rerun is warranted solely to regenerate it.

### Strengths

- The evidence gives concrete fixture transitions, including 20 retired identities returning to zero and an identity surviving the in-flight batch before pruning on the next full sweep, while explicitly limiting the conclusion to set-retention behavior (`plans/090-verification-evidence.md:6-9`).
- The staging explanation is grounded in the actual schema, writer, cleanup, and journal-retention paths (`plans/090-verification-evidence.md:11-14`). Focused source inspection confirmed the cited behavior: `agent_tool_calls` rows are written as `claude`, are removed after a linked operation or seven-day expiry, and keep their conversation while present; the Codex/OpenCode live-set query uses retained operations and unresolved batch identities (`TranscriptMatcher.cs:24,418-447,498-529`; `JournalSchema.cs:282-289`; `JournalRetention.cs:211-215`).
- Gate and formatter summaries match the archived direct outputs: Windows gate exit 0 with UI 361, Core 4,599/11 skipped, MCP 489/4 skipped, and 9m04s; branch formatting reports two changed C# files and exit 0 (`windows-gate/test-SqlHarness.Tests.log:1-22`; `windows-gate/test-SqlHarness.Mcp.Tests.log:1-13`; `windows-gate/ui-check.log:1-20`; `gate-console.log:1-7`; `task-1-fix-format.log:1-3`). The documented UI warnings are disclosed rather than described as pristine output (`plans/090-verification-evidence.md:21`).
- The plan, README, history, and evidence consistently leave the final whole-branch review pending and state that no merge or push is authorized (`plans/090-matcher-title-backfill-retention.md:11-13,52`; `plans/README.md:20`; `plans/history.md:3-5`).

### Issues

#### Critical (Must Fix)

None.

#### Important (Should Fix)

None.

#### Minor (Nice to Have)

- `plans/090-verification-evidence.md:21` calls all five regression-first failures “expected failures,” although the sentence then clarifies that two were over-specific assertions corrected before implementation. Only three failures exposed the stated retention defects. Rephrase to distinguish the three defect regressions from the two corrected assertions so the RED evidence is exact.

### Assessment

**Task quality:** Approved

**Reasoning:** The documentation accurately records the implementation’s lifetime boundary and the available verification results, and it keeps the final review status open as intended. The RED-run wording needs a small precision edit; it does not undermine the underlying retention evidence.
