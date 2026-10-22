# Plan 101 whole-branch review

Scope: read-only review of the complete working-tree diff against `2b0530b65e87fafe8cd09d05756389a56d8c0989`, including the reviewed ProcessRunner test-helper integration, plan/index, README and verification evidence. Product patch identified by SHA-256 `8524C505FD788F9EA273A90DAF3E4851A7430AFA0D481216EFBE79132DAB4053`. No tests were rerun and no source, index, HEAD or branch state was changed. This review report is the sole authored artifact.

## Strengths

- The new control reuses the existing full-document save queue. Both coupled transitions are atomic, and disabling annotations preserves journal and retention values. Tests cover loaded/default state, cross-card preservation, queued edits and rejected saves.
- Config parsing now rejects the invalid pair with a field-specific error before disk writes; the demonstrated backend gap justifies this small addition. Existing valid configuration and default-off behavior remain intact.
- The helper fix is limited to test publisher diagnostics. It retries transient IO failures, retains the original publisher error on non-IO marker failures, and adds focused regressions without altering production ProcessRunner.
- Evidence distinguishes the two pre-fix failed gates from the final passing Windows gate and records warnings, skipped tests and the uncommitted tree accurately. Archived final summary reports exit 0, all five stages, UI 372 passed, Core 4621 passed/11 skipped and MCP 496 passed/4 skipped. MCP host/context inspection supports the process-start configuration claim.

## Issues

### Critical

None.

### Important

1. **Explain that enabling annotations also enables recording.** `src/SqlHarness.Dashboard/ui/src/pages/SettingsPage.tsx:319`: the help says only “Requires the activity journal”; it never tells the operator that switching annotations on automatically turns the disabled journal on. The handler at line 318 does exactly that. The Fix contract explicitly requires explaining this behavior beside the toggle. An operator can reasonably read “Requires” as a prerequisite they must enable themselves; enabling a separate recording setting deserves explicit disclosure. Add adjacent wording such as “Enabling agent annotations also enables the activity journal.” Add an assertion to the existing guidance test so this requirement is retained.

### Minor

- Existing deferred evidence wording remains: `task-2-report.md:17` says true/30 “survived”, whereas browser JSON records omitted initial disk properties and demonstrates those effective values only after saving. Treat this as post-save/default-value evidence, not direct proof of explicit initial disk values. It does not undermine the paired-save acceptance or the source/API preservation checks.
- Node DEP0190 and the Vitest advisory are disclosed non-failing diagnostics; no pristine-output claim is warranted. No additional blocker arises from them.

## Declined to judge

- Linux execution: outside the required Windows completion gate; no Linux run or Linux compatibility conclusion is claimed.
- Live database annotation authorization, mutation approval, historical-data deletion and sensitive-storage behavior end to end: no live database run is required or authorized for this review. The changed paths do not modify those policies; source/diff inspection and archived regression results support preservation, not a fresh live certification.
- Reliability of unchanged process-tree cancellation across every Windows load/timing condition: the helper race fix and its tests were inspected, but the final passing gate cannot prove universal absence of flakes. There is no concrete remaining defect identified in the integration.
- Explicit initial on-disk preservation of compare/timeout browser values: the initial artifact omits those keys; effective defaults and post-save values must not be presented as captured explicit initial values.

## Assessment

**Ready to merge? With fixes.** One bounded guidance requirement remains unmet. The paired-state implementation, backend persistence and test-helper integration otherwise have no identified Critical or Important defect. Add the automatic-journal-enable explanation, run the relevant focused UI check on that final change and obtain scoped re-review; retain the existing full-gate evidence subject to the repository/user rules on final product-tree changes. Commit, merge and push remain separately unauthorized.
