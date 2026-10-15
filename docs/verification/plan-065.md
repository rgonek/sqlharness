# Plan 065 synthetic artifact plan pagination evidence

This record measures only emitted MCP response text for generated public safe fixtures. The integration tests create and remove local fixture directories, run the saved plan reader and MCP result adapter, and measure response text with `McpResultAdapter.MeasureBytes` (UTF-8). Token figures are labeled estimates using `ceil(emitted UTF-8 bytes / 4)`; they are not tokenizer measurements. Fixture construction, tool discovery/capabilities, input tokens, and final chat are unmeasured. No database is opened.

## Before and after: small plan

The fixture is a single saved `baseline` repetition and statement: one synthetic root plus 36 synthetic child operators, for 37 nodes total. It uses generated names (`Synthetic-001` etc.), `public.fixture_###` child object names and a `public.synthetic_fixture` root, a harmless generated predicate, and statement text `SELECT fixture_value FROM public.synthetic_fixture`. Selector: `variant=baseline`, `statement=0`, default last measured repetition, `includeText=false`, starting `cursor=0`. Response budget: 65,536 bytes.

| Reader source window | Calls to reconstruct all paths | Emitted UTF-8 response bytes | Estimated tokens (`ceil(bytes/4)`) |
|---:|---:|---:|---:|
| 32 nodes (before) | 2 | 9,173 | 2,294 |
| 128 nodes (after) | 1 | 8,749 | 2,188 |

The before run returned 32 nodes, then 5; the after run returned all 37 with `continuation: null`. These are output-volume observations for this fixture and selector only. They do not establish general token savings or reduced workflow cost.

## Budget-bound reconstruction and edge cases

A separate generated 257-node plan was read from `cursor=0` under a 4,096-byte response budget. Both runs reconstructed all paths 0 through 256 exactly once, advanced each continuation by the number of nodes actually returned, included the final page, and kept every response within budget. An additional read from `cursor=5` verified that the first returned path was `0/4` and the continuation matched cursor plus returned nodes; that probe is excluded from the totals below.

| `includeText` | Calls | Emitted UTF-8 response bytes | Estimated tokens (`ceil(bytes/4)`) |
|---|---:|---:|---:|
| `false` | 18 | 66,654 | 16,664 |
| `true` | 20 | 76,364 | 19,091 |

The no-fit case used one generated node carrying 24 synthetic warning strings of 512 characters each, `includeText=true`, cursor `0`, and a 4,096-byte budget. The adapter returned an empty node page, `continuation: null`, and the explicit `pageError`; response size was 545 UTF-8 bytes (estimated 137 tokens).

These measurements were produced by:

```powershell
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter FullyQualifiedName~McpTokenBudgetTests --logger "console;verbosity=detailed"
```

The direct reader and capability checks were run with:

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~Plan_section_selects_measured_repetitions_and_paginates_every_flat_node|FullyQualifiedName~Capabilities_lists_artifact_command_and_read_limits"
```

## Historical workflow observation

The previously observed 14 calls remain historical context: 6 first reads + 6 continuation reads + 2 repeated reads. They are distinct from the generated synthetic measurements above and were not remeasured or attributed to this fixture.

## Verification and adoption decision

P1's clipping-count invariant is supported by the focused reader, CLI, and MCP regression tests and the full solution suites. P2 is supported for the measured synthetic cases: the 37-node fixture fits in one response, and the 257-node fixture reconstructs all paths exactly once under the 4,096-byte budget, including `includeText=true`, nonzero cursor, final page, and no-fit behavior. These fixtures support the bounded pagination change; they do not establish general call, byte, or token savings. The historical observation remains 14 calls = 6 first pages + 6 continuations + 2 repeated agent reads.

Windows `verify.ps1` first stopped at `ui-check`: the unchanged `CompareSessionOverview.test.tsx` case exceeded its 20-second limit (160 passed, 1 failed). The same file passed in isolation (7/7), and a single full retry passed all stages: UI 161/161; restore; build with 0 warnings/errors; Core 3,494 passed/11 skipped; MCP 374 passed/4 skipped; format. Before the retry, the scoped format check reported final-newline diagnostics in seven changed C# files; an EOF-only correction received review and the retry's format stage passed.

The first requested `verify-linux.ps1` run synced committed tip `40a6871d` and stopped at `ui-check` with the same unchanged 20-second UI timeout (160 passed, 1 failed). One authorized full retry at the same committed base passed: UI 161/161; restore; build with 0 warnings/errors; Core 3,492 passed/6 skipped; MCP 370 passed/3 skipped; format. The retry validates the committed base, not the uncommitted implementation. To validate the implementation, the exact working-tree snapshot was separately copied to ext4. Its UI, restore, build, and test stages passed: UI 161/161; build 0 warnings/errors; Core 3,499 passed/6 skipped; MCP 375 passed/3 skipped. The raw Windows-byte copy preserved CRLF, so its first Linux format check reported 19 whitespace diagnostics across seven files. `git ls-files --eol` confirmed `i/lf w/crlf attr/text=auto`, including unchanged files. Repeating only format on a second snapshot with those text files normalized to Git's Linux LF checkout form passed. Thus the retry is a green Linux gate at committed base `40a6871d`; the implementation snapshot's ext4 equivalent stages passed after applying Git's normal text checkout conversion.

Snapshot identifiers: raw archive SHA-256 `3f99050d5616ef36d95e50f9a23524a51285391ad7de1437ad054437b3959c40`; raw extracted-file manifest SHA-256 `bc4a50cc9ff590de47343559ccfdf1c9d155183861e978e72cd63c0e5205aff4`; Git-normalized manifest SHA-256 `57c54b3b10a774f01b04ed7f00479115cc9519e565bbe287ca71fa3c539b87c9`. These identify the validation snapshot captured after the reviewed EOF correction. Later verification, plan/index, and skill wording edits did not change implementation or test source bytes. The normalized copy changes only CRLF to LF for `text=auto` paths. No database was opened. The implementation is committed as `0b6dce4`; the feature branch is verified as recorded below. Feature-to-main integration and verification on integrated `main` remain pending authorization.

## Additional feature-branch verification (2026-10-15)

The feature branch had `main` merged in commit `488598d`. Windows verification ran first on that revision:

| Command | Exit | Stages and results |
|---|---:|---|
| `just verify` | 0 | UI install 1m15s; UI check 2m39s; restore 32.9s; build 2m07s; tests 4m24s (Core 3,511 passed/11 skipped; MCP 374 passed/4 skipped); format skipped because the clean working tree had 0 changed C# files |

The first `just verify-linux` at `488598d` printed `OK stages=9 16.1s`, but this was a false green and is not verification evidence. The shared clone stayed at `8849049`, `FETCH_HEAD` remained on `main`, and the stage logs were empty. The feature's then-current Linux gate used a local `$lines` accumulator whose case-insensitive name shadowed the caller's `$Lines` parameter, leaving each Linux stage script empty. It also lacked reliable native `wsl.exe` output and exit capture. The reviewed gate-only fix `8849049` was cherry-picked as `7acf039`; no unrelated main commits were brought in. The focused `pwsh ./tests/scripts/gate-output.Tests.ps1` passed 13/13.

At `7acf039`, corrected Linux attempt 1 synced the clone to the exact revision, passed sync/SDK/Node/UI install, then failed UI check (exit 1, 4m37s). Its log contained only `wsl exited with 1`, so the cause is unknown. A bounded direct rerun of the same UI command against the unchanged clone passed: 35 files and 163 tests, exit 0, 50.30s; existing chart container-size warnings were emitted. One full retry was authorized and run at the same revision; no further retry was made.

| Corrected retry stage | Result |
|---|---|
| sync | passed, 0.6s; clone HEAD verified as `7acf0395bd3fef494833d2c46e0f00fc77819aa8` |
| SDK | passed, 0.8s |
| Node | passed, 0.4s |
| UI install | passed, 23.7s |
| UI check | passed, 1m05s; 35 files, 163 tests |
| restore | passed, 8.3s |
| build | passed, 1m18s; 0 warnings/errors |
| test | passed, 5m18s; Core 3,516 passed/6 skipped/3,522 total; MCP 375 passed/3 skipped/378 total |
| format | passed, 2m02s; all 7 changed C# files |

The corrected retry ran as `just verify-linux -LogDirectory <dedicated-log-directory>`; it exited 0 with `verify-linux: OK stages=9 10m16s`. Its stage output is retained under the corrected-retry log set. The earlier corrected attempt's unknown UI check failure remains part of the record. No database was opened. This proves the feature tree at `7acf039`; no feature-to-main merge or integrated-main gate was performed.
