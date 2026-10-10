# SDD ledger — plan: docs/superpowers/plans/2026-10-10-mcp-protocol-upgrade.md
Base: e580821; branch feat/mcp-protocol-upgrade; worktree D:/Dev/sqlharness/.worktrees/mcp-protocol-upgrade.
Spec: docs/superpowers/specs/2026-10-10-mcp-protocol-upgrade-design.md.
Preflight table:
| Pair or task | Producer / consumer or internal agreement | Finding |
|---|---|---|
| 1/2 | Host constants and options; removal of temporary pin and test references | Ordered dependency; temporary pin expressly allowed in Task 1. |
| 1/3 | Host options and identity placeholder; Task 3 fills and registers it | Ordered dependency. |
| 1/4 | Host version constants; capabilities consumes them | Ordered dependency. |
| 1/5 | Server options and supported revisions; smoke tests consume them | Ordered dependency. |
| 1/6 | Host options and stderr logger in same file | Separate methods; ordered edits. |
| 2/3 | Host references in same file; identity holder independent | No conflict. |
| 2/4 | Mapper fallback introduced then replaced by negotiated value | Ordered dependency. |
| 2/5 | Stdio harness optional revision consumed by smoke tests | Ordered dependency. |
| 2/6 | Stdio harness consumed by stderr tests | Ordered dependency. |
| 3/4 | Identity and capabilities tool handlers | Both use request server, no conflicting contract. |
| 3/5 | Identity consumed by smoke tests | Ordered dependency. |
| 4/5 | Capability version consumed by smoke tests | Ordered dependency. |
| 4/7 | Capabilities fields consumed by acceptance script | Ordered dependency. |
| 5/6 | Revision process harness consumed by stderr tests | Ordered dependency. |
| 6/8 | Stderr behavior documented | Ordered dependency. |
| 7/8 | Acceptance script documented | Ordered dependency. |
| Task 1 | Tests, filter, factory, temporary pin | Internally consistent; stream fallback expressly allowed. |
| Task 2 | Mechanical pin removal, MCP suite | Internally consistent. |
| Task 3 | Request identity and journal test | Internally consistent. |
| Task 4 | Mapper and handler updates, tests | Internally consistent. |
| Task 5 | Revision smoke and EOF theory | Internally consistent. |
| Task 6 | Stderr test and logger level filter | Internally consistent. |
| Task 7 | Tap and driver, manual acceptance | Publishing local binary is outside worktree; review before execution. |
| Task 8 | Docs and sequential gates | Linux sync caveat expressly specified. |
Ruling: Task 1 2026-07-28 negotiation test uses server/discover rather than initialize — the SDK rejects that revision on initialize and the spec requires discover — if wrong, the smoke coverage may need redesign.
Task 1: fix round 1/5 (2 addressed, 0 open; commits e3074e6..63c8703)
Task 1: complete (commits e580821..63c8703, review clean)
Task 2: complete (commits 63c8703..fe87eb5, review clean)
Task 3: fix round 1/5 (1 addressed, 0 open; commits 33982bf..3c231a3)
Task 3: minor (deferred): agent_kind/source retain first call classification when initial metadata is absent.
Task 3: complete (commits fe87eb5..3c231a3, review clean)
Task 4: minor (deferred): request-scope capabilities forwarding lacks wire assertion; Task 5 request-scope smoke may cover.
Task 4: complete (commits 3c231a3..fb9ed54, review clean)
Task 5: complete (commits fb9ed54..a28dade, review clean)
Task 6: complete (commits a28dade..644be8d, review clean)
Ruling: Task 7 acceptance uses a worktree-local publish output passed via -Sqlharness, not publish-local replacing the global executable — avoids side effects outside worktree while exercising the same build — if wrong, installed binary packaging differences remain untested.
Task 7: fix round 1/5 (4 addressed, 1 open; commits c6e91da..72ff048)
Ruling: Task 7 requires explicit -Sqlharness and updates the plan interface/run command — prevents a stale PATH binary during acceptance — if wrong, the one-command published-install workflow needs restoration.
Task 7: fix round 2/5 (0 addressed, 1 open; commits 72ff048..e67c295)
Task 7: fix round 3/5 (1 addressed, 0 open; commits e67c295..5ec2df7)
Task 7: complete (commits 644be8d..5ec2df7, review clean)
Task 8: fix round 1/5 (1 addressed, 0 open; commits b809f54..8652d02)
Task 8: complete (commits 5ec2df7..8652d02, review clean)
Final review fix: acceptance now journals one target-free `gain` call per real client, reads the row from that client's isolated `SQLHARNESS_HOME`, and matches persisted client name/version to the observed `clientInfo`; request-scope capabilities smoke asserts the negotiated revision.
Final review disposition: `agent_kind`/`source` can retain process-tree fallback classification if an early request omits `clientInfo`; parked because this protocol-upgrade requirement concerns first-non-null `client_name`/`client_version`, which are now verified for both real clients.
