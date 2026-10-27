repo: rgonek/sqlharness
branch: main
path: src/SqlHarness.Dashboard/ui/src

## Last sync
date: 2026-10-27T14:29:15Z

### Updated in this project
- Linked prototype: Activity (1c) → Session (4a) → Operation; old variants in archive/
- Session: note above Optimization, conversations in Details, operations list

## Screen map
| Screen | Repo files |
| --- | --- |
| Usage.dc.html | pages/StatsPage.tsx, pages/ProfileDimensionsPanel.tsx, lib/stats.ts |
| Profiles.dc.html | pages/ProfilesPage.tsx |
| Mutation Approval.dc.html | mutations/ApprovalToast.tsx, mutations/ApprovalDialog.tsx, mutations/mutationStore.ts, components/SafetyNoticeToast.tsx |
| Operation.dc.html (v2) | pages/OperationPage.tsx, components/plan/*, components/optimization/* |
| Table IO Variants.dc.html | components/optimization/StatementRows.tsx |
| Operation.dc.html | pages/OperationPage.tsx, components/VariantPanel.tsx, components/SqlBlock.tsx, components/plan/*, components/optimization/StatementRows.tsx |
| Settings.dc.html | pages/SettingsPage.tsx, lib/settingsRules.ts, mutations/sound.ts |
| Activity.dc.html | components/AppLayout.tsx, pages/LivePage.tsx, pages/SessionsPage.tsx, pages/OperationsPage.tsx, pages/NamedOperationsPage.tsx, pages/MutationsPage.tsx, components/OperationTable.tsx, components/SessionTable.tsx |
| Session.dc.html | pages/SessionPage.tsx, components/AppLayout.tsx, components/BrandLogo.tsx, components/SessionAnnotationHeader.tsx, components/optimization/OptimizationSection.tsx, components/optimization/VersionTable.tsx, components/optimization/ComparisonPanel.tsx, components/optimization/StatementRows.tsx, components/SessionVersionDiff.tsx, components/SqlDiff.tsx, components/sql-editor/monaco.tsx, components/plan/PlanGraphView.tsx, components/plan/planGraphLayout.ts, components/plan/PlanStatementSwitcher.tsx, components/plan/planSourceLinks.ts, api/types.ts, components/ReproScriptButtons.tsx, components/ui/*.tsx, index.css |

## Sync history
- 2026-10-24T15:52:00Z — Operation, Settings
- 2026-10-24T14:06:03Z — Activity (1a–1c, 2a–2b, 3a–3c)
- 2026-10-24T12:02:04Z — Session Optimization turns 3–4 (header, keep/label, Plan tab)
- 2026-10-24T07:55:27Z — turns 1–2 (current layout recreation, 1b–1d, 2a–2b)
