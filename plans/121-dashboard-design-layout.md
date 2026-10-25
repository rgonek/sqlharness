# Plan 121: Dashboard design layout

Status: DONE; locally integrated into main at `fc83e808`; `just publish fast` completed and installed binary verified. No push. Original base `443f468e`; current main `05a414a4` incorporated before the final gate.

## Approved references

The operator requested implementation of the Claude Design export and selected
Activity 3a (the 1c session feed and 3a filters), Operation v2, and Session
Optimization turn 4 including its final IO tab. Settings uses the six-section
export. The selected references are in
[docs/design/dashboard-2026-10-25](../docs/design/dashboard-2026-10-25/README.md).

## Scope and constraints

Implement the layouts in the current React dashboard. Preserve live reconciliation,
uncapped running operations, history pagination, exact case/parameter-set plan
links, SQL readability, operator mutation approval and settings confirmations.
Do not invent metrics, stored results or approval history from reference fixtures.
Preserve the operator's dirty main documents. The operator subsequently authorized local merge into main and `just publish fast` after green verification. Do not push.

## Work

1. Import the selected references and record the version decisions.
2. Consolidate navigation and implement Activity's session feed and filters.
3. Implement operation tabs and the session SQL diff / IO / Plan workbench.
4. Regroup settings into Database, Mutations, Journal & privacy, Agents,
   Dashboard and Model prices.
5. Verify interactions, review the changes and run the required Windows
   `just verify` gate on the final product tree.

## Verification

See [verification](121-evidence/verification.md), [implementation](121-evidence/implementation.md) and [reviews](121-evidence/reviews.md). Browser comparison remains unverified.
