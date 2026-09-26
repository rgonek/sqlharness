# SQLHarness agent output contract

Status: implemented in part by implementation plan 03, Task 1.

## Version 1 envelope

`--output agent` writes one compact JSON document followed by a newline to stdout:

```json
{"schemaVersion":1,"command":"query","status":"success","exitCode":0,"result":{},"error":null,"truncation":null}
```

The envelope has these fields:

| Field | Meaning |
|---|---|
| `schemaVersion` | Integer contract version, currently `1`. |
| `command` | Invoked SQLHarness command in lowercase. |
| `status` | `success`, `error`, or `partial`. `partial` means a compare matrix stopped after one or more completed cells; the report contains those cells and `error` identifies the failed next cell. A terminal outcome such as watch max duration or snapshot differences uses `error` status while retaining its complete report. |
| `exitCode` | Existing numeric process exit code. |
| `result` | Command report, or `null` when no report exists. |
| `error` | `null` on success; otherwise the safe error object below. |
| `truncation` | Reserved for bounded agent projection; currently `null`. |

Error objects contain stable string `code`, `phase`, and safe `message`. Optional `hint` and `location` may be added without changing the envelope. Codes describe cause classes and never use exception class names. Current codes include `safety_rejected`, `input_file_unavailable`, `authentication_failed`, `target_mismatch`, `sql_execution_failed`, `local_storage_failed`, `watch_max_duration`, `snapshot_differences`, and `operation_failed`.

The safe error must not disclose SQL, parameter values, tokens, or connection strings. Error serialization does not parse exception message text to choose a code. A future projection may add safe locations and recovery hints.

## Existing JSON switches

Successful `--json` and `--json-summary` responses keep their existing report shape. On failure they emit one JSON object with `result` and the same structured `error` object used by agent output. Text remains the default. `--output agent`, `--json`, and `--json-summary` are mutually exclusive. Help and version retain Spectre's raw text output.

## Partial matrix results

When a compare matrix stops at its first failed cell, completed cells stay in `result`, and the failure is present in `error`. The envelope status is `partial`. Later cells are not run.

## Projection and truncation

Agent output defaults to a 16 KiB UTF-8 budget for the complete envelope, including its final newline. `--max-output-bytes` accepts 4096..1048576 and `--max-cell-chars` accepts 0..4096 (default 512). These switches apply to `--output agent`; existing `--json` and `--json-summary` output remains unchanged.

When the response must be reduced, projection keeps fields in this order: status and error; result correctness and equivalence; metric availability; primary metrics; paths for artifacts that were actually written and omission counts; then detailed rows, cells, warnings, sets, and matrix entries. The projection does not alter raw result hashes, equivalence inputs, or snapshot completeness. It does not create a query result artifact. If query result rows are omitted from the response, the report retains `resultHash` and row omission counts, and makes no claim that a full result file exists.

Large detail lists, including plan operator trees, are bounded before JSON serialization using a conservative aggregate detail allowance derived from the response and cell budgets. String cells, warnings, paths, command names, and error text are clipped to the requested cell limit. The envelope includes `truncation.omittedItems` and the applied detail and cell limits when projection removes content. Noteworthy operators remain capped at ten with the existing warning/spill/conversion priority. Agent-capable report types have bounded projections; a future unrecognized report is represented by a small typed omission summary instead of being serialized unbounded or replaced by an output error. If even the minimum structural envelope cannot fit, SQLHarness emits a small structured `output_budget_too_small` error instead of cutting JSON bytes. The minimum configured budget is 4096 bytes. Validation and parser errors use the same configured output and cell budgets.
