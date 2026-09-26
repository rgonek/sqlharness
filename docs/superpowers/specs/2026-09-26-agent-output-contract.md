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

Task 1 does not impose a byte or cell limit. Task 3 defines bounded projections and truncation behavior; until then `truncation` is `null` and agent output serializes the current report compactly.
