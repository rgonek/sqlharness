# SQLHarness agent output contract

Status: schema version 2, shared by MCP and CLI `--output agent`.

## Envelope

Each response is one compact JSON document in one text block, followed by a newline:

```json
{"schemaVersion":2,"command":"query","status":"success","exitCode":0,"result":{},"error":null,"truncation":null}
```

The envelope fields are `schemaVersion` (currently `2`), lowercase `command`, `status`, numeric `exitCode`, `result`, `error`, and `truncation`. Status is `success`, `partial`, `error`, `watch_max_duration`, or `snapshot_differences`. A partial compare matrix keeps completed cases and reports the failed next case in `error`. Error objects carry stable `code`, `phase`, and safe `message`, with optional safe `hint` and `location`.

## Budgets and projection

The complete text block is budgeted by its UTF-8 byte count, including JSON escaping and the final newline. MCP calls and CLI `--output agent` default to 65536 bytes. MCP's process ceiling is set with `--max-result-bytes` (default 262144; range 4096..1048576); a call may raise or lower its budget up to that ceiling. The CLI `--max-output-bytes` accepts 4096..1048576. MCP's tools/list catalog has a separate 32768-byte budget. Cell clipping defaults to 512 characters; CLI `--max-cell-chars` accepts 0..4096.

> **Accepted follow-up, 2026-10-14 (operator):** [plan 067](../../../plans/067-cell-limit-parity-and-default.md)
> raises the default to 2048 on both surfaces and adds MCP `maxCellChars` 0..4096 (raise or lower per call),
> for CLI/MCP parity. Accepted planning only, not implemented; the sentence above describes current behavior.

Every report tries detail limits in this order: `unbounded, 128, 64, 32, 16, 8, 4, 2, 1, 0`. If a report is reduced, `truncation` reports the selected `detailLimit`, `omittedItems`, and applied `maxCellChars`. JSON is not cut mid-document; if the minimum envelope does not fit, the result is `output_budget_too_small`.

Query projections retain the resolved database and column `valueType` aliases for CLR values; they do not infer SQL types. Each clipped retained cell has a `clippedCells` entry with its result-set-local row and column, original and returned lengths, and unit. Omitted rows have no cell entries. At detail level zero, query projection keeps fitting set inventories first, then whole schemas, then complete rows, followed by leading column and row prefixes.

Matrix projections keep a lightweight, ordered `cases` inventory ahead of optional per-case details whenever it fits. Oversized inventories page with a continuation cursor. Per-table logical reads are ordered by descending median reads, descending maximum, then table name. Saved artifact `statements` and `matrix-cells` sections support cursor-based pages.

## Context accounting

Emitted bytes count the UTF-8 bytes of the text the model reads. The token estimate is bytes divided by four and rounded up; it is an output-size estimate, not actual model tokenization. MCP scenario comparisons may separately include tools/list discovery and follow-up artifact pages.
