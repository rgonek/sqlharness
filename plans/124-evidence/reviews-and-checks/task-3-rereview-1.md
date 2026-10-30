### Original findings

- **Stale revision — ADDRESSED:** `src/SqlHarness.Dashboard/ui/src/pages/ProfileEditForm.tsx:10-12,35` captures the editable snapshot and its revision for the lifetime of the form. The refresh/conflict test exercises the old revision and retained draft (`ProfilesPage.test.tsx:173`).
- **TLS transition — ADDRESSED:** `ProfileEditForm.tsx:41` resets trust, mode and root certificate on engine changes; `ProfilesPage.test.tsx:187` covers both directions, including SQL Server trust enabled to PostgreSQL.
- **In-flight editing — ADDRESSED:** `ProfileEditForm.tsx:40,60` disables editable controls and actions while pending; `ProfilesPage.tsx:45,61-62` blocks route and profile changes. The deferred-response test covers control disabling (`ProfilesPage.test.tsx:203`).
- **Hidden validation errors — ADDRESSED:** `ProfileEditForm.tsx:28-30,59` shows otherwise unmapped errors and associates select errors with their controls; `ProfilesPage.test.tsx:220` exercises both paths.

### New findings in fix diff

- None.

### Unrelated pre-existing observation

- `src/SqlHarness.Dashboard/ui/src/pages/ProfilesPage.tsx:47-48,50,70` still derives the mounted form from the live query. A background refresh that returns missing/invalid data, removes the selected profile, or fails can unmount the dirty draft without confirmation. This is distinct from the addressed same-profile revision mismatch and predates this fix; preserve the editing snapshot across such refreshes and test these scenarios if included in scope.

### Assessment

**Scoped fix review: Approved.** All four original findings are addressed; no new breakage identified in the fix diff. The reported focused output shows 20 tests passed and typecheck completed; tests were not rerun. Full final gate and live acceptance remain outside this review.
