## Task 1: Session, Operation, IO and Settings visual corrections

**Files:** UI SessionOperationsTable/OwnerStarButton/StatusBadge, VersionTable, IoComparison, OperationPage and associated components, SessionNoteCard, SettingsPage, scoped index.css and focused tests. Do not modify StatsPage, mutation files or backend.

- [ ] Read spec items1-6,10-13,16, current mocks and source; use shadcn skill/official docs Button/Table/Tabs/Field/ToggleGroup.
- [ ] Add focused regression coverage for compact row label/star/spark/running, single-extra IO column toggle, native A/B selection and distinct instances, matching Details, and preservation of Settings validation/confirmation. Reuse SSE Session tests; verify auto-refresh via real cache invalidation path, not polling assertions that mirror implementation.
- [ ] Make Session descriptions one compact line without repeated target prose; retain full label/SQL/target through title or existing detail navigation. Time HH:mm:ss with full timestamp accessible. Larger Hugeicons star action, spark for keep, running Badge. Preserve flags, returnSession, internal toggle and pagination.
- [ ] Place version label inline with vN. Restore visible circular A/B labels over real native radio inputs, preserving focus/keyboard and semantics. Use existing variant utilities/semantic colors rather than raw theme skins.
- [ ] Expose the sole IO extra (LOB logical reads in statement view) beside Scans; keep multi-option whole-batch selection if useful. Statement disclosure inherits row background, hover and focus preserve whole-row behavior.
- [ ] Align Operation tab/parameter header and Details with Session. Avoid global toolbar/overlay regressions.
- [ ] Add Play/Refresh/Plus icons to Preview/Refresh/Add note; keep accessible names/workflows. Improve retention Field inset and right-align transcript clear action responsively.
- [ ] Run relevant focused tests/typecheck/lint/build; self-review and commit only owned paths. Report beside brief with exact tests, commits and screenshot disposition. No full gate or plan/index changes.
