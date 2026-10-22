# Plan 097 whole-branch review

Range: `ebf7a16..6d658da` (local main after Plan 096 to final Plan 097 product head). Reviewed the Fix contract, SDD scope rulings, Task 2 and 3 reports, task review, merge report, source, served interface changes, and regression tests. This is an independent read-only product review; the report is the only file written.

## Strengths

- Collection minima change to one while the 32-case maximum and pre-connection rejection remain in Core, CLI, MCP mapping, and both served fixed/request schemas. Explicitly empty sets remain distinguishable from omitted sets through `SqlHarnessModule`.
- Named singleton measurement stays on the set-report path. The compare runner now carries the selected set name into existing run artifacts; matrix cells retain their index and existing matrix identity. The focused tests inspect persisted compare artifacts, journal reproduction conditions, typed NULL and non-scalar inputs, setup/repeat counts, and failure coverage.
- The 32+1 regression uses served MCP calls over a stream, counts all 33 distinct inputs and existing warmup/repeat executions, and recovers two distinct artifacts. Its reported bytes are transport bytes from the fixture, with no live connection path or model-token claim.
- The current README and MCP input/tool docs describe the new bounds and split-call behavior. Plan 096 ingress limits are retained in the combined descriptions.

## Issues

### Critical

None found.

### Important

None found in the reviewed product diff.

### Minor

None found.

## Declined to judge

- Final Windows `just verify` result: the gate was still running during this review, so its exit status and final tested tree require the controller's later evidence.
- Final plan/index/history and verification-evidence completion: these were being written after the gate and are outside the reviewed product head.
- Live database/provider behavior and actual-agent token efficiency: the acceptance evidence deliberately uses fake sessions and transport byte counts; neither was run or claimed here.
- Historical `Fixtures/OperationAnnotations/off/tools-list.json` snapshot: it is archived pre-change fixture evidence, not the currently served schema.

## Assessment

**Ready to merge? With fixes** — product review has no blocking implementation finding. Acceptance remains conditional on a successful final Windows gate and complete plan/evidence documentation at the tested tree. No push, merge, paid-agent trial, or live database call was assessed or authorized by this review.
