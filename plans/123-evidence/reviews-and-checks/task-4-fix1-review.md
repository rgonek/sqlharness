### Spec compliance: ✅ Approved

- **ADDRESSED — Important:** OptimizationSection.tsx:85-93,118-119 preserves groupB only when B exists and keeps a validated explicit A when invalid B or a 404 is cleared. OptimizationSection.test.tsx:269-291 exercises non-default A with unset B and stale B; the 404 test also verifies A preservation at line 265.
- **ADDRESSED — Minor:** VersionTable.test.tsx:47-53 asserts the null row's Rows cell contains a dash and separately checks explicit variation.
- **New fix-diff findings:** None.

### Task quality: Approved

The fix addresses both findings with targeted behavior tests. The report records 21 focused tests passing and diff check passing; tests were not rerun for this review. Full verification and browser acceptance remain Task 5.
