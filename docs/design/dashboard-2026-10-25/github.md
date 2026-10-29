repo: rgonek/sqlharness
branch: main
path: src/SqlHarness.Dashboard/ui/src

## Last sync
date: 2026-10-24T15:52:00Z

### Updated in this project
- New Operation.dc.html: simplified operation page (header + KPIs + tabs SQL & plan / Statements / Timing / Details)
- Settings.dc.html regrouped into 6 sections

## Screen map
| Screen | Repo files |
| --- | --- |
| Operation.dc.html | pages/OperationPage.tsx, components/VariantPanel.tsx, components/SqlBlock.tsx, components/plan/*, components/optimization/StatementRows.tsx |
| Settings.dc.html | pages/SettingsPage.tsx, lib/settingsRules.ts, mutations/sound.ts |
| Activity.dc.html | components/AppLayout.tsx, pages/LivePage.tsx, pages/SessionsPage.tsx, pages/OperationsPage.tsx, pages/NamedOperationsPage.tsx, pages/MutationsPage.tsx, components/OperationTable.tsx, components/SessionTable.tsx |
| Session Optimization.dc.html | pages/SessionPage.tsx, components/AppLayout.tsx, components/BrandLogo.tsx, components/SessionAnnotationHeader.tsx, components/optimization/OptimizationSection.tsx, components/optimization/VersionTable.tsx, components/optimization/ComparisonPanel.tsx, components/optimization/StatementRows.tsx, components/SessionVersionDiff.tsx, components/SqlDiff.tsx, components/sql-editor/monaco.tsx, components/plan/PlanGraphView.tsx, components/plan/planGraphLayout.ts, components/plan/PlanStatementSwitcher.tsx, components/plan/planSourceLinks.ts, api/types.ts, components/ReproScriptButtons.tsx, components/ui/*.tsx, index.css |

## Sync history
- 2026-10-24T14:06:03Z — Activity (1a–1c, 2a–2b, 3a–3c)
- 2026-10-24T12:02:04Z — Session Optimization turns 3–4 (header, keep/label, Plan tab)
- 2026-10-24T07:55:27Z — turns 1–2 (current layout recreation, 1b–1d, 2a–2b)
