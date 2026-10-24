# Plan 104 live experiment report

Date: 2026-10-24. Checkout: `D:\Dev\sqlharness\.worktrees\plan-104`.
Scope: bounded live STATISTICS IO, actual-plan XML, and same-session metadata observations. No product code or tests changed.

## Result

The tested SQL Server build emitted table-variable IO names as bare generated `#8hex` tokens. Explicit local temp tables emitted the user name followed by SQL Server's internal underscore-and-hex suffix, both at the top level and inside a session-local temporary procedure. In every explicit-temp case in this matrix, a same-session `OBJECT_ID` lookup resolved to the exact suffixed `tempdb.sys.objects.name` also shown by STATISTICS IO.

This makes the raw suffix a candidate provenance signal for this one tested build and these workloads. It does not establish a version-independent contract. The current capture path normalizes away the suffix before either IO dictionary is built; the explicit `#ABCDEF12` then becomes `#ABCDEF12`, an 8-hex-shaped key accepted by the table-variable name heuristic. The live observation therefore confirms a concrete false-positive path in shape classification after normalization. It does not prove that preserving the suffix is sufficient for every SQL Server version and cached-object path.

Actual plan XML contained typed object references for the measured statements: table variables appeared as `[@v]`; explicit temp tables appeared as `[#ABCDEF12]` and `[#p104_temp]`. This matrix issued separate simple statements against each object, so plan XML reveals the type and statement text. The existing IO parser has no per-counter statement or plan-object association, so the plans do not themselves establish a general mapping for arbitrary multi-object statements.

## Environment and boundary

The target was SQL Server `17.0.1145.4`, edition `Standard Developer`, engine edition `3`, reached as `localhost` using Windows integrated authentication in `tempdb`. SQLCMD used a 5-second connection timeout, 30-second command timeout and `-b`; `-y 0` was added so STATISTICS XML output was not truncated. The exact successful command and capture hashes are in [`successful-run-manifest.json`](experiments/successful-run-manifest.json). The original stdout and stderr are [`matrix.stdout.txt`](experiments/matrix.stdout.txt) and [`matrix.stderr.txt`](experiments/matrix.stderr.txt); the parsed case/IO/plan-object index is [`summary.json`](experiments/summary.json), with each actual plan saved as `plan-NNN.xml` beside it.

Each of the seven cases used three repetitions. Each individual temp object contained at most 1000 rows; the stored-procedure case used 333 rows in each of its three objects (999 total per invocation). All tables and the temporary procedure were session-local. Catalog lookups filtered by `OBJECT_ID('tempdb..#known_name')`, so they resolved only the current session's exact object identity rather than enumerating other sessions' temporary objects. The database metadata lookups themselves generated `sysschobjs` STATISTICS IO lines; these are labeled in the raw result window and are not counted as target-object observations.

An execution-bound deviation occurred: while trying to refresh plan-cache fields in the summary, I invoked the runner without its intended analyze-only mode and it executed the full matrix a second time. The second raw capture, 66 extracted plans, summary, exit code and hashes are retained under [`runs/20261024T080219Z`](experiments/runs/20261024T080219Z/summary.json). Thus six repetitions per case were executed across two matrix runs, three in each. No further SQL execution occurred. Both captures remained within localhost/tempdb and the per-run row/time bounds, and no persistent object, database setting, configuration or service was changed. The second run corroborates the first; conclusions below use the first three-repetition capture. The runner now requires `-Mode Execute` to access SQL Server and `-Mode AnalyzeCapturedRun` to parse the saved first capture.

The successful first capture had exit 0, 445,176 stdout bytes, empty stderr and SHA-256 values bound in its manifest. A preliminary script without `GO` separators failed at compile time with `Msg 2714` because the same static `#ABCDEF12` name occurred in one compilation batch. A separate executable-path quoting attempt also failed before SQLCMD ran; its stale `matrix.exit.txt` is retained as an attempt artifact and is not the successful run's exit. Neither setup failure changed the database.

## Observations

| Case | Raw STATISTICS IO observation | Plan/catalog observation |
|---|---|---|
| One table variable, three dynamic batches, 1000 rows each | Three distinct bare names: `#A3D092CF`, `#A4C4B708`, `#A5B8DB41`. Each insert reported 1001 logical reads and each count query 2. | Plan objects were `[@v]`; generated names changed between invocations. |
| Two table variables, three batches, 500 rows each | Each run produced two distinct bare `#8hex` names. The two inserts reported 500 reads apiece; each count read one page per object. | Plan objects named `[@v1]` and `[@v2]`; summing both within a run is observable, while their generated IO names vary by run. |
| Explicit `#ABCDEF12` at top level, 1000 rows | Raw table name was `#ABCDEF12` followed by 107 underscores and `000000000003`, for setup and each of three reads. The parser's suffix rule reduces this to `#ABCDEF12`. | Scoped `OBJECT_ID` resolved the identical internal catalog name. Plan object was `[#ABCDEF12]`. |
| Conventional `#p104_temp`, 1000 rows | Raw table name was `#p104_temp` followed by 107 underscores and `000000000004`, for setup and all reads. | Scoped catalog name matched; plan object was `[#p104_temp]`. |
| Mixed explicit `#ABCDEF12` plus `@v`, 500 rows each | Explicit IO name was `#ABCDEF12` + 107 underscores + `000000000005`; table-variable names were bare `#B406FA98`, `#B4FB1ED1`, and `#B5EF430A` across repetitions. | The explicit and variable objects were separate in plan XML as `[#ABCDEF12]` and `[@v]`. The data show distinct raw signatures in this case, then loss of that suffix distinction at normalization. |
| Nested dynamic batch loop, three executions, 1000 rows | Variable names changed across runs: `#B6E36743`, `#B7D78B7C`, `#B8CBAFB5`; each count query reported two logical reads. | Plans contained `[@v]`. |
| Session-local procedure `#p104_probe`, three calls, 333 rows per object | On every call, `#ABCDEF12` emitted `#ABCDEF12` + 107 underscores + `000000000006`; `#p104_temp` emitted `#p104_temp` + 107 underscores + `000000000007`; `@v` emitted bare `#BC9C4099`. The three names stayed stable across calls. | Catalog IDs and exact suffixed names stayed stable across calls. Plans contained `[#ABCDEF12]`, `[#p104_temp]` and `[@v]`. In the second capture, all 24 plans from this case reported `RetrievedFromCache="true"`; all three insert/select object identities remained available in the plans. |

IO values include writes and setup statements as well as the selected reads. The row counts above describe inserted rows, not a claim that all reported logical reads came only from the final SELECT. The `sysschobjs` lines come from the labeled metadata queries.

## Classification implications

The actual ambiguity assertion is established at the existing parser boundary: the explicit identifier `#ABCDEF12` is a valid explicit local temp table name, SQL Server reports it with an internal suffix, and `TempTableKey.Normalize` turns it into `#ABCDEF12`. The current shape classifier then cannot tell that normalized explicit-temp key from any generated bare `#XXXXXXXX` table-variable key. Therefore, classifying normalized `#8hex` names as table variables is not positively grounded.

Raw suffix presence separated the two object kinds in all tested statements, including the cached temporary-procedure calls. That is promising source evidence for preserving the pre-normalized STATISTICS IO name or adding a provenance field before normalization. The experiment cannot establish that every relevant SQL Server release or execution shape always emits that suffix for explicit temp objects. It also did not observe a cached explicit temp reported as a bare eight-hex token, nor could it force a table-variable name to a chosen literal token. Do not promote the observed suffix distinction to a universal engine guarantee from this one-version run.

The plans provide object-kind evidence (`@` variable reference versus `#` temp reference) for each plan statement. A future capture design can investigate whether a statement-scoped association from STATISTICS IO messages to actual-plan objects remains unambiguous for joins, repeated objects and cached plans. The present parser aggregates table names from message text and has no such association. A direct `OBJECT_ID` lookup worked for explicit temp objects and recovered the exact suffix-bearing catalog name; it did not supply an equivalent mapping for generated table-variable IO names.

The safe current conclusion is: reject name-shape aggregation after suffix normalization. Preserve raw per-counter spelling and investigate capture-time suffix provenance or validated plan association before implementing positive table-variable aggregation. This report does not implement the original median repair and does not claim the live results prove universal classification.

## Reproduction and remaining limits

Inspect the generated SQL in [`matrix.sql`](experiments/matrix.sql). The initial matrix was run using:

```powershell
& 'C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\180\Tools\Binn\SQLCMD.EXE' `
  -S localhost -E -C -d tempdb -l 5 -t 30 -b -y 0 `
  -i plans/104-evidence/experiments/matrix.sql
```

Use `powershell -NoProfile -File plans/104-evidence/experiments/run.ps1 -Mode AnalyzeCapturedRun` to parse the retained initial output without DB access. `-Mode Execute` runs the matrix and writes a new timestamped evidence directory. The runner's execution mode is included for reproduction on a fresh authorized experiment environment; running it again on the captured target would add another three repetitions beyond the approved total.

The evidence covers one SQL Server build, one database, one session, seven controlled patterns and a small row cap. It did not test older SQL Server releases, alternate compatibility levels, concurrent sessions, other table-variable declarations, all cache/recompilation paths, or the complete SQLHarness capture pipeline. No product tests or repository gate were run because this change contains experiment evidence only.
