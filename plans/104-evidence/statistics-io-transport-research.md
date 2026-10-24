# STATISTICS IO transport and exposed identity

Checked: 2026-10-24. This note concerns the public transport contract, separately from the empirical object-naming rule.

## Documented facts

1. SQL Server's official ODBC documentation identifies STATISTICS IO output as diagnostic message **3615**, obtained after the result set is drained: [Processing statements that generate messages](https://learn.microsoft.com/en-us/sql/relational-databases/native-client-odbc-error-messages/processing-statements-that-generate-messages?view=sql-server-ver17).
2. Microsoft's published message catalog defines 3615 as a formatted table-name string followed by IO counters. Its template has no separate object-kind or object-ID field: [official documentation source, row 3615](https://github.com/MicrosoftDocs/sql-docs/blob/live/docs/relational-databases/errors-events/includes/sql-server-2019-database-engine-events-and-errors-3000-3999.md#L525).
3. The TDS INFO token carries message number/state/class, message text, server/procedure names and source line number. There is no object-ID or object-kind field in this token: [MS-TDS section 2.2.7.13 INFO, Token Stream Definition and Parameter Details](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-tds/284bb815-d083-4ed5-b33a-bdc2492e322b).
4. The public STATISTICS IO documentation describes the output Table item as the table name, without specifying temporary-object name decoding: [SET STATISTICS IO, Output](https://learn.microsoft.com/en-us/sql/t-sql/statements/set-statistics-io-transact-sql?view=sql-server-ver17#output).

## Consequence for SQLHarness (inference)

Changing the diagnostic-message consumer cannot reveal a hidden typed object identity in INFO: its public layout has none. A typed identity must therefore be derived from the raw server-provided name under an explicitly scoped naming rule, or correlated with separate server evidence. The absence of such fields in INFO does not prove that no other SQL Server diagnostic facility can provide them.

The current parser reduces raw temporary names before forming string-key dictionaries ([Diagnostics.cs](../../src/SqlHarness.Core/Diagnostics.cs)). Preserving the raw name is a distinct issue from the transport limitation and is supported by the new experiments. Neither this transport note nor a single-build experiment is a Microsoft promise that all bare #8hex names in every execution context are table variables.
