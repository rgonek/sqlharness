**Empty case picker raw identity and missing transition coverage** — ADDRESSED — CompareDetail.tsx:82-85 sets the Select value to null when there are no selectable cases; CompareDetail.test.tsx:28-56 rerenders with the same external selectedCase after complete options disappear and asserts the “No cases available” placeholder, no options, and the empty-state message.

**Sound preference domain ID assertion** — ADDRESSED — SettingsMutations.test.tsx:107-111 now asserts that choosing “Glass drop” calls saveSoundPreferences with selection: "glass-drop".

### New Breakage in the Fix Diff

- None found in the changed code or tests.

### Out-of-Scope Observations

- SettingsPage and StatsPage long-label browser layout remains assigned to Task 11 integrated browser coverage, per the controller’s instruction; this fix does not change those surfaces.

### Verdict

**Fix round:** All findings addressed, no new Critical/Important breakage.

### Checks

- Read review-582a807..fe20a52.diff once; it contains only the two fixes and their regression/assertion tests.
- Read existing fix-round logs: focused tests report 2 files / 16 tests passed; typecheck and lint completed. I did not rerun suites.
