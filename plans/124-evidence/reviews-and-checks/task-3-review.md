### Spec Compliance

- ❌ Issues found: the form can submit an old draft against a newer revision, silently carry an insecure TLS interpretation across engine changes, and discard edits made during an in-flight save. See Important findings.
- ⚠️ Full final verification, end-to-end real profile acceptance, and cross-task server security are outside this task review; Task 4 owns the final gate. Focused tests and typecheck were reported, not rerun.

### Strengths

- `src/SqlHarness.Dashboard/ui/src/pages/ProfileEditForm.tsx:30-31` omits inherited mode and serializes only the selected writer; a separate writer draft is preserved while switching choices.
- `src/SqlHarness.Dashboard/ui/src/pages/ProfilesPage.tsx:42-44,69-70` guards ordinary dirty navigation and hides editing with the server-provided unsupported-platform explanation. Errors retain the current form in the tested 400/409/500 cases.

### Issues

#### Critical (Must Fix)

- None.

#### Important (Should Fix)

- `src/SqlHarness.Dashboard/ui/src/pages/ProfilesPage.tsx:69` and `src/SqlHarness.Dashboard/ui/src/pages/ProfileEditForm.tsx:10-11,31`: the form initializes its draft only once but receives the live query revision on every render. If the Profiles query refreshes during editing (e.g. reconnect/refetch), the same keyed form keeps the old draft while its `revision` prop advances. Save then supplies the new revision and overwrites the other writer's changes to that profile without a 409. Capture the original revision with the draft on opening and retain it through Save; test a query refresh while dirty and verify the stale write is rejected.
- `src/SqlHarness.Dashboard/ui/src/pages/ProfileEditForm.tsx:36,40-41`: switching SQL Server to PostgreSQL retains `trustServerCertificate` while `sslMode` stays unset. A valid SQL Server profile with trust enabled therefore changes to the PostgreSQL legacy setting that disables TLS, without a visible warning or an explicit TLS choice. `DashboardProfiles.cs:132-136` confirms the changed interpretation. Reset or explicitly resolve the engine-specific TLS setting on transition and test both directions, particularly SQL Server trust=true to PostgreSQL.
- `src/SqlHarness.Dashboard/ui/src/pages/ProfileEditForm.tsx:28-32,35-55`: Save disables only Save and Cancel; all form controls remain editable while the request is pending. Edits made after submission are omitted from the request and then silently discarded when success closes the form and clears dirty state. Freeze all editable controls until the save settles, or preserve changes and leave the form open; test a deferred response with an intervening edit.

#### Minor (Nice to Have)

- `src/SqlHarness.Dashboard/ui/src/pages/ProfileEditForm.tsx:27,46,54`: backend `sslMode` errors become invisible when SQL Server is selected, and the generic mapping silently drops any field without a rendered error slot. Include an accessible fallback error summary and connect select errors using `aria-describedby`, so validation remains discoverable even for hidden or unexpected fields.

### Assessment

**Task quality:** Needs fixes.

**Reasoning:** Core mode/writer serialization and basic navigation are implemented, but the revision and in-flight editing defects can lose user changes; engine transition can silently weaken transport security. Checked the query defaults for refresh behavior, the backend TLS interpretation, and the server validation error field names as focused cross-file risks. The interrupted full UI run is not final-tree verification.
