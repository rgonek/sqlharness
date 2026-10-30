### Remaining metadata finding: ADDRESSED

- ProfileWriter.cs:14,33-35 makes the public save path Windows-only and returns SecurityMetadata before locking, reading the target or creating a temporary file on other platforms. The speculative Linux statx/listxattr implementation was removed. The Windows restricted-DACL replacement path is retained; its focused test passed in round 1.
- DashboardProfiles.cs:22-26 exposes availability and its reason on every ProfilesResponse, and DashboardProfiles.cs:99 returns the fixed unavailable message for an unsupported save. Task 3 must consume these fields and Task 4 must document the Windows-only limitation.

### New issues in scoped fix diff

None identified.

### Evidence and assessment

- Reported Windows focused results: 33 passed, 0 failed, 0 skipped (plans/124-evidence/logs/task2-review3-final2.log). The simulated unsupported-platform test verifies refusal without a temp file or changed bytes; this is not native Linux or macOS runtime verification. No test suite was rerun.
- **Task quality: Approved for this scoped review.** The ruling accepts reduced availability as the safe resolution. Full Windows gate, UI behavior, product documentation and actual non-Windows runtime verification remain outside this review.