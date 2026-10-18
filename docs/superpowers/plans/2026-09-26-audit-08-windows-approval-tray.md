# Windows approval tray Implementation Plan

**Status:** Superseded by plan 073 (dashboard approval); tray deferred.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add to our SQLHarness MCP one-time approval of persistent DML in a local Windows application with a tray icon and notifications.

**Architecture:** The tray is a local broker of decisions and execution over Core. MCP may report, check and cancel a request, but cannot approve it through the API. A neutral rules library will later allow replacing the Windows UI/IPC on Unix.

**Tech Stack:** .NET consistent with the completed plan 07, C#, WinForms NotifyIcon, Windows App SDK notifications, System.IO.Pipes, System.Text.Json, xUnit; Windows 11 x64 as the first target.

**Spec:** [Windows approval tray design](../specs/2026-09-26-windows-approval-tray-design.md), [roadmap](2026-09-26-audit-roadmap.md).

## Global Constraints

- After the complete 07 and its predecessors. Only our MCP, without a global proxy.
- Exact immutable SQL + parameters + target + policy; every change requires a new one-time approval.
- No response/expiry/restart means no new execution. No mutation retries, also after an uncertain result.
- Persistent DDL, dynamic SQL and other forbidden constructs remain forbidden despite approval.
- The Windows UI approves locally; IPC and MCP have no Approve method. Do not promise isolation from any arbitrary process under the same Windows account.
- SQL/parameters only in memory and the local preview; journal and notifications without their content.
- The default MCP remains without persistent mutations. The --approval-mode tray mode is enabled at startup by the operator.

## Review Focus

- TOCTOU: changing the SQL/profile/parameters file between preview and execution: T1–T3.
- Replay, double click, restart and a failure between SQL and the result write: T2–T3.
- IPC spoofing, a foreign-scope requestId and an attempt to set approved=true: T4–T6.
- Sleep/lock/DND, a hidden notification and a very long batch: T5.
- An error/timeout after a partial write, cancelling Executing and a benchmark collision: T3/T6–T7.

## File structure

| Planned file/project | Responsibility |
|---|---|
| `src/SqlHarness.Approvals/SqlHarness.Approvals.csproj` | library neutral for UI and platform |
| `ApprovalRequest.cs`, `ApprovalStateMachine.cs`, `ApprovalDigest.cs` | immutable requests, states and approval binding |
| `ApprovalJournal.cs`, `ApprovalBroker.cs`, `ApprovedMutationExecutor.cs` | persistent consumption, decision and a single Core execution |
| `ApprovalProtocol.cs`, `ApprovalClient.cs` | typed Submit/Status/Cancel without Approve |
| `src/SqlHarness.Approval.Windows/SqlHarness.Approval.Windows.csproj` | Windows executable, separate package |
| `Program.cs`, `TrayApplicationContext.cs`, `ApprovalQueueForm.cs`, `ApprovalDetailForm.cs` | single instance, tray and UI |
| `WindowsNotifications.cs`, `WindowsApprovalPipe.cs`, `WindowsSessionState.cs` | toast, IPC security, lock/sleep |
| `src/SqlHarness.Mcp/Tools/MutationTool.cs` | a single optional tool and scope/gate integration |
| `tests/SqlHarness.Approvals.Tests/` | neutral rules and broker tests |
| `tests/SqlHarness.Approval.Windows.Tests/` | Windows IPC/activation/UI |
| `docs/approval-tray.md` | installation, configuration and protection boundaries |

The new names are target names. In T1 agree the actual interfaces of the completed Core/MCP and do not create a second parser/binder. Separate the Windows projects from the cross-platform build/test.

## T1 — immutable request contract and threat model

**Files:** ApprovalRequest.cs, ApprovalDigest.cs, ApprovalProtocol.cs; spec; new ApprovalRequestTests.cs, ApprovalDigestTests.cs.

**Interfaces:** the contract Submit(Proposal)→ApprovalStatus, GetStatus(requestId, scope)→ApprovalStatus, Cancel(requestId, scope)→ApprovalStatus. A Proposal carries a requestKey and materialized content with typed parameters, not a path to an arbitrary file. Decision is not part of the public protocol.

- [ ] Write the bindings of the real 07 APIs, the 01 safety rules and 02 target/TLS, along with commits. Confirm Windows 11 x64 and the chosen way of packaging notifications.
- [ ] Add digest tests: a change of SQL, statement order, parameter value/null/type, target, timeout or policy changes the digest; reordering the input order of parameter names with the same canonical map does not change meaning.
- [ ] Freeze the request and the independently resolved target; the UI callback must not re-read a mutable DTO/file. Test changing sources after Submit.
- [ ] Test that the API contains no approve/allowMutation/confirmDatabase; unknown fields are rejected. Define an enumeration of stable states and codes in accordance with the spec.
- [ ] Run `dotnet test tests/SqlHarness.Approvals.Tests/SqlHarness.Approvals.Tests.csproj`; local commit `feat: define immutable approval requests`.

## T2 — state machine, journal and approval consumption

**Files:** ApprovalStateMachine.cs, ApprovalJournal.cs, ApprovalDigest.cs; ApprovalStateMachineTests.cs, ApprovalJournalTests.cs.

**Interfaces:** the journal must atomically perform pending/approved→consumed before SQL and recover state without restoring the right to execute. The local UI calls the decision inside the broker; it does not return an authorizing token to MCP to consume.

- [ ] Transition table tests: Pending→Denied/Expired/Cancelled; Approved→Executing only once; competing approve/cancel/expiry have a single winner. A double click does not start a second attempt.
- [ ] TTL 120 s, configurable 30..600; start within 30 s after the decision and before expiry. Test a fake clock and sleep/resume/clock rollback, without relying solely on the wall clock to extend validity.
- [ ] Write a versioned journal with atomic publish/flush; test fail-before-write/fail-after-write/fail-on-flush. No persistent consumed → no ExecuteAsync.
- [ ] Restart: Pending/Approved→Invalidated, consumed without a result→OutcomeUnknown. Test the dedupe key+digest and a conflict for other content. Retention 30 days without storing SQL and values.
- [ ] Test that synthetic values do not appear in journal/log/error; the HMAC key does not reach IPC/MCP. Run the tests and local commit `feat: persist single-use approval state`.

## T3 — broker executing exactly the accepted batch

**Files:** ApprovalBroker.cs, ApprovedMutationExecutor.cs; existing Core composition after 05; new ApprovedMutationExecutorTests.cs.

**Interfaces:** the executor accepts only an internal consumed request received from the state machine, not a bool approved. The Core call has AllowMutation=true/ConfirmDatabase computed only in the broker. No public "execute this approval" endpoint.

- [ ] Test no DB execution for Pending/Denied/Expired/Cancelled/Invalidated, an incorrect scope and a changed digest.
- [ ] Test a single ExecuteAsync after approval, with the same SQL and values, and the check of the actual database before SQL. Persistent DDL/cross-database/unsupported function remains a rejection even on a click.
- [ ] The broker uses its own connection configuration for the same approved target; an MCP→broker mapping mismatch ends in rejection. Passwords/tokens never return to MCP/UI.
- [ ] Test a partial batch execution, a SQL error, a lost response, cancelling Executing and a failed result write. The response must not claim "no changes" without evidence; no retry, OutcomeUnknown when needed.
- [ ] Test of the scope slot: at most one mutation, no open transaction/connection while waiting. Do not introduce an implicit rollback wrapper.
- [ ] Run the new tests and the safety/Core regressions; local commit `feat: execute approved mutations through the broker`.

## T4 — local IPC and process binding

**Files:** ApprovalProtocol.cs, ApprovalClient.cs, WindowsApprovalPipe.cs; WindowsApprovalPipeTests.cs and ApprovalProtocolTests.cs.

**Interfaces:** a local versioned named pipe; Submit/Status/Cancel methods with bounded length framing, not serialization of arbitrary .NET types. Maximum materialized proposal 16 MiB in total; reject the excess before full allocation. Connection limit, handshake deadline 5 s.

- [ ] Establish the ACL, rejection of network clients, user/session/peer PID and installation path control in both directions. Test pipe squatting and an incorrect process. Do not treat PID/CurrentUserOnly as cryptographic evidence of a human decision.
- [ ] Test malformed frames, an unknown version, oversized length, disconnecting mid-way, Submit replay and a requestId of another scope. Do not log an invalid payload.
- [ ] Do not register Approve over IPC; fuzzing the decision/approved/allowMutation fields must result in rejection, never in a state change.
- [ ] Establish a non-sensitive journal scope mapping after reconnect; a broken connection invalidates pending approvals, and an executing one receives a best-effort cancellation with the uncertain result preserved.
- [ ] Run the neutral protocol tests and the Windows IPC tests on Windows; local commit.

## T5 — tray, queue, preview and operator decision

**Files:** all Windows UI files from the table; new ApprovalViewModelTests.cs and WindowsApprovalUiTests.cs; docs/approval-tray.md draft.

**Interfaces:** the UI renders an immutable ApprovalRequest from the broker; the local decision method checks the current requestId/digest/state/expiry on every click. A toast opens the details or rejects, it never approves.

- [ ] Build a single application instance, a tray icon, a queue of up to 20 pending/5 scope, history and pause. Do not open a new window for each request; aggregate notifications without losing entries.
- [ ] The window shows the full SQL and typed parameters, the actual target, OUTPUT targets, timeout/expiry and multi-statement nature. Parameter values are revealed only locally; without passwords, webview and executing links from SQL/model rationale.
- [ ] Buttons "Approve once"/"Reject"; Enter does not approve, closing the window accepts nothing. Tests of a stale view after expiry, a double click and a removed request.
- [ ] A generic notification without data; the activation argument only navigates. Test DND/disabled notifications: the queue still works. Test lock/unlock and suspend/resume: no decision in a locked session, TTL without extension.
- [ ] Test a large batch, a final DELETE below the first screen and an incomplete preview: the UI must not approve a preview truncated by an error. Ensure keyboard access, high DPI readability and focus without a default acceptance.
- [ ] Manual Windows acceptance: screenshots of synthetic data only; run the view-model/UI tests. Local commit `feat: add Windows tray approval UI`.

## T6 — optional mutation tool of our MCP

**Files:** `src/SqlHarness.Mcp/Tools/MutationTool.cs`, catalog/options/scope/gate from 07; new McpMutationTests.cs and McpApprovalScopeTests.cs in the MCP test project.

**Interfaces:** `sqlharness_mutation` action request/status/cancel in accordance with the spec. Startup --approval-mode tray explicitly expands the catalog from 11 to at most 12; the result and list budgets are still 16/32 KiB. The public query remains read-only.

- [ ] Test mode off: no tool and every attempt at persistent DML is still blocked. Tray mode without a broker: APPROVAL_UNAVAILABLE, without fallback to Core mutation.
- [ ] Request returns pending quickly; the model receives requestId/expiry, not an authorization token. Status/cancel cannot accept SQL/a new target/decision. Polling no more often than every 2 s; no Submit retries with a new key by the adapter.
- [ ] In tray mode coordinate the MCP gate and the broker per scope. First acquire the slot and the persistent consumption, only then execution; a read benchmark/mutation collision gives BUSY without a DB call. Pending does not block ordinary reads. Test loss of the gate owner, reconnect and several MCP instances of the same scope.
- [ ] Test the new states against outputSchema and isError: a pending request and a correct retrieval of denied/unknown have an explicit state; no confusion with execution success. An execution error is described separately from the success of a status read.
- [ ] The result goes through the shared projection/redaction and budgets; no SQL/parameters/credentials in status. Gain counts the execution once in the broker, status polling separately as cost, without multiple success of the same mutation.
- [ ] Run the MCP tests, the schema/token budget and the neutral broker tests; local commit `feat: gate MCP mutations on local Windows approval`.

## T7 — end-to-end evidence, Windows package and documentation

**Files:** new WindowsApprovalEndToEndTests.cs, opt-in integration of both engines; Windows solution filter/job and release workflow; docs/approval-tray.md, README.md, AGENTS.md, spec 07 with the extension version marking.

**Interfaces:** a separate tray package for win-x64, the existing MCP binary preserves cross-platform operation in off mode. The test client uses real stdio and IPC, and SQL is a fake executor by default.

- [ ] E2E: tools/call request → Pending → local test decision in the UI process → exactly one attempt → status. Test control of the decision is available only through dependency injection in tests, never an endpoint in release.
- [ ] E2E rejection/expiry/no tray/restart/pipe spoof/file change/cancellation; an offline attempt demonstrates the absence of an executor call for all rejections.
- [ ] After authorization of one-time SQL Server/PG databases, check the real DML effect, target control, the absence of execution after a rejection and the uncertain result after a lost response. Do not use production profiles; record synthetic evidence.
- [ ] Build/test the CLI and MCP on Linux/macOS without the Windows runtime; the Windows release checks the tray, notifications activation, update and registration cleanup. Do not install the application or autostart for the user without a separate order.
- [ ] Documentation: installation/start/pairing, one-shot, TTL, partial changes, unknown outcome, local retention and the trust model. No claim of exactly-once or of protection of all agent tools.
- [ ] Final review of the whole consent→consume→execute path, test results and the list of live checks not performed. Local commit; no push.

## T8 — preparation of a later Unix port (without UI implementation)

**Files:** new `docs/superpowers/specs/2026-09-26-approval-unix-port-notes.md` created after Windows acceptance, when the real module boundaries are known.

- [ ] Write down the adapters requiring replacement: notifications/tray, session lock, IPC peer identity, the HMAC key and packaging; preserve the neutral state machine/journal/executor.
- [ ] Separate the Linux desktop and the macOS menu bar as well as headless → no approvals and no execution. Do not assume a single tray API for every environment.
- [ ] Identify the neutral tests that transfer unchanged and the new platform tests. A separate Unix implementation plan after the decision on the first platform; the current scope ends with a working Windows.

## Definition of done

On Windows the operator can see, approve once or reject exactly our MCP mutation. No decision, request substitution, retry and failure do not lead to a silent execution. Uncertain effects are explicit. An ordinary MCP without the extension enabled remains without persistent mutations. Documentation without implementation and evidence does not mean completion of this plan.
