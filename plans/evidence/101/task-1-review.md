### Spec Compliance

- ✅ Spec compliant. The UI binds `operationAnnotations` to the journal settings, uses the existing save path, sends valid paired booleans when annotations or the journal are toggled, preserves the value during another card save, and gives the required restart and retention guidance (`SettingsPage.tsx:305,311,314,315`; tests in `SettingsPage.test.tsx` and `DashboardSettingsTests.cs`). The shared parser rejects `operationAnnotations: true` when the journal is disabled with a field-specific error (`SqlHarnessConfig.cs:173,174`). The added parser validation is justified: the API regression test covers the invalid pair returning `BadRequest` without changing the existing file.
- ⚠️ Cannot verify from this diff: the running MCP process continues using its process-start configuration until restart/reconnect, and that existing sensitive-storage, mutation-approval, retention, and MCP-authorization behavior remains correct end to end. The diff adds the UI guidance and does not change those runtime paths; resolve these cross-cutting checks in the planned browser/full-branch verification.

### Strengths

- The journal transitions are atomic: enabling annotations while the journal is off enables both together, disabling the journal disables annotations in the same save, and re-enabling the journal leaves annotations off (`SettingsPage.tsx:305,311,312`; `SettingsPage.test.tsx`, transition and queued-save cases).
- Tests cover default and loaded values, the paired transitions, queue ordering, rollback-visible save failure, unrelated-card preservation, API round-trips, and rejected invalid writes (`SettingsPage.test.tsx`; `DashboardSettingsTests.cs`).
- The shared validation is at the configuration parser used by the existing Settings PUT path; the existing MCP contract states that annotations require the journal (`DashboardSettings.cs:27,29`; `docs/mcp/tools.md:29`). No endpoint or configuration key was added.
- Reported focused UI tests passed 28/28, API tests passed 19/19, and typecheck passed. These were not rerun during this review.

### Issues

#### Critical (Must Fix)
None.

#### Important (Should Fix)
None.

#### Minor (Nice to Have)
- `.superpowers/sdd/101-dashboard-operation-annotations-setting/task-1-report.md:35` reports an intermediate Vite chunk-size warning while the final incremental run was clean. Keep the final verification evidence focused on the clean run, or identify the warning as baseline with its source; it does not block this task.

### Assessment

**Task quality:** Approved

**Reasoning:** The implementation follows the requested Settings API and journal save queue, keeps coupled values valid in each transition, and has focused tests for the behavioral edges. The only recorded issue is a non-blocking warning in an intermediate build; runtime and broader policy checks belong to the planned end-to-end verification.
