# Plan 077: WebAuthn verification for mutation approvals

> This work is deferred from plan 073. Read [plan 073](073-mutation-approval-broker.md) and the [mutation approval design](../docs/superpowers/specs/2026-10-14-mutation-approval-design.md) before implementation.

**Status:** TODO. This plan is separate so the click-only mutation broker can ship and be reviewed without making authenticator support a prerequisite.

**Dependency:** plan 073 DONE. Until this plan is implemented, `click` is the only supported verification value. Requests to enable WebAuthn must fail visibly; the application must not silently downgrade to click.

**Goal:** Add optional WebAuthn verification to the existing dashboard mutation approval flow, including authenticator enrollment, assertions bound to an approval digest, credential lifecycle, safe Settings transitions, and documented recovery.

## Scope

- Select a maintained WebAuthn library compatible with the repository's .NET target. Confirm current package support and license before adding it; do not hand-roll protocol verification.
- Add registration options/completion and assertion options endpoints, guarded with the dashboard's existing request protections.
- Bind approve challenges to the request digest, make each challenge single-use, and enforce a short expiry.
- Store credentials in an owner-only SQLHarness home file using the established owner-only file helper.
- Require an existing credential assertion before enrolling another credential. Require an assertion to disable WebAuthn. Require at least one enrolled credential before enabling it.
- Update the approval dialog and Settings flow; record `click` or `webauthn` verification in journal history.
- Extend the agent-side hygiene hook to block access to `webauthn-credentials.json` and calls to `/api/webauthn/` on the dashboard loopback origin.
- Document recovery for lost authenticators and test malformed, expired, reused, and cross-request assertions.
- Add an available-browser smoke check where the operator's platform supports Windows Hello, Touch ID, or a security key.

## Boundaries

- Do not change the mutation broker's request, digest, decision, timeout, or execution guarantees from plan 073.
- Do not treat browser support for WebAuthn as a reason to bypass the server-side verification setting.
- Do not fall back to click when enrollment or assertion fails. Return a clear error and leave the request pending until its normal expiry or operator denial.
- Recovery must be explicit and auditable; deleting or losing credential data must not silently lower the verification mode.

## Completion evidence

- Tests prove an assertion for one request cannot approve another, a challenge cannot be reused, and expired challenges are rejected.
- Tests prove the first credential can enroll, a second enrollment requires an existing assertion, and enabling/disabling WebAuthn enforces the credential/assertion requirements.
- Dashboard tests cover successful and rejected approvals; Settings tests cover configuration and recovery guidance.
- Documentation explains supported authenticators, the user-verification policy, credential storage, and recovery.
- Run the repository verification gates required by `docs/agents/verification.md` and report actual results.
