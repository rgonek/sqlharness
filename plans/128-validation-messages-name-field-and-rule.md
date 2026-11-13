# Plan 128: Name the failing field and rule in profile-var and compare-context validation errors

> **Executor:** use staged implementation, task review and whole-branch review. This is a plan, not authorization to implement, merge, push, install or access a live database.
>
> **Drift check first:** `git diff --stat 85d1c95e..HEAD -- src/SqlHarness.Core/Targets/TargetResolver.cs src/SqlHarness.Core/CompareContext.cs tests docs/mcp`. Reconcile changed messages against the excerpts below. Work in an isolated `.worktrees/plan-128` checkout.

## Status and authority

- PENDING. Planned at `85d1c95e`, 2026-11-12. Not approved for implementation.
- Priority P3; effort S; implementation risk LOW; category ux.
- Dependencies: none. Independent of Plan 127.
- **Spec:** the Fix contract in this file is authoritative. Error codes and phases stay unchanged; only message text changes.
- Follow `AGENTS.md`, `plans/README.md`, `docs/agents/verification.md`; MCP changes also read `docs/mcp.md`.

## Why this matters

Two validation errors say that a value is wrong without saying which field or which rule, so each one cost an agent a corrective call:

- A profile var with a prefixed value (`release: "x-12345"` where the rule allows digits only) returned `safety_rejected` with "Profile var 'release' does not match its validation rule." The rule is not visible to the client, so the fix is a guess.
- `compare` with an uppercase `subject` returned "Compare context labels must be 1..64 lowercase ASCII letters…". The message does not say whether `subject` or `scope` failed. `capabilities.compareContext.labelPattern` documents the rule but not the field.

## Current state and conventions

- `Targets/TargetResolver.cs:81-103` iterates `profile.Vars` as `(name, pattern)` and throws `SqlHarnessSafetyException($"Profile var '{name}' does not match its validation rule.")` on a mismatch. The supplied value is not echoed, and must stay that way.
- `CompareContext.cs:17-18` checks `subject` and `scope` together and throws one `ArgumentException`.
- Pinned by `tests/SqlHarness.Mcp.Tests/McpMappingTests.cs:800` and `tests/SqlHarness.Tests/Cli/MeasureParameterSetCommandTests.cs:145` (`Contains("Compare context labels")`). Find other pins with a search for both strings before editing.
- Disclosure: `docs/mcp/startup.md:86` says request mode reports no variable values, resolved databases or credentials. A validation pattern is operator-authored configuration of an already allowlisted profile, not a value, database name or credential.

## Fix contract

1. Profile var mismatch: `Profile var '{name}' must match the profile rule {pattern}.` Never include the supplied value. Other var messages (null value, invalid regex, timeout) stay unchanged.
2. Compare context: validate `subject` and `scope` separately. The message names the field the caller used: MCP `subject`/`scope`, CLI `--subject`/`--scope`. Keep the rule text: `Compare {field} must be 1..64 lowercase ASCII letters, digits, '.', '_' or '-', starting with a lowercase letter or digit.` When both fail, report `subject` first.
3. Error code `safety_rejected` and phase `validation` stay unchanged.

## Review Focus

- A var whose value contains the pattern text must not be echoed.
- A pattern containing characters that are special in JSON or Markdown must arrive intact in the MCP error envelope.
- An invalid `scope` with a valid `subject` must name `scope`.
- CLI and MCP must name the field the way each caller spells it.
- Redaction must not mask the pattern unless it matches a known secret.

## Scope

Source: `src/SqlHarness.Core/Targets/TargetResolver.cs`, `src/SqlHarness.Core/CompareContext.cs` (add a field-name parameter, or map the field at the MCP and CLI call sites; choose the smaller change).
Tests: `tests/SqlHarness.Mcp.Tests/McpScopeTests.cs`, `McpMappingTests.cs`, `tests/SqlHarness.Tests/Cli/MeasureParameterSetCommandTests.cs`, `tests/SqlHarness.Tests/CompareContextTests.cs`.
Docs: `docs/mcp` only where the old message text is quoted.

### Task 1: Profile var message names the rule

- [ ] Test `Request_var_mismatch_names_the_rule_without_the_value` in `McpScopeTests.cs`: a synthetic profile var `release` with rule `^[0-9]{4,8}$` and request value `x-12345` returns `safety_rejected` whose message contains `Profile var 'release' must match the profile rule ^[0-9]{4,8}$` and does not contain `x-12345`.
- [ ] Change the message per Fix contract 1.
- **Verify:** `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "FullyQualifiedName~McpScopeTests"` RED, then GREEN.

### Task 2: Compare context message names the field

- [ ] Tests in `CompareContextTests.cs`: `Invalid_subject_is_named` ("Compare subject must be…"), `Invalid_scope_is_named` ("Compare scope must be…"), `Both_invalid_names_subject_first`.
- [ ] Update the existing pins at `McpMappingTests.cs:800` and `MeasureParameterSetCommandTests.cs:145` to the field-specific text. Add one CLI assertion that the message names `--subject`.
- [ ] Implement per Fix contract 2.
- **Verify:** `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~CompareContextTests|FullyQualifiedName~MeasureParameterSetCommandTests"` and the MCP mapping filter GREEN; then Common verification from `docs/agents/verification.md`.

## STOP conditions

- An operator or documentation rule says profile validation patterns are private: stop and report before exposing them.
- A client or test depends on the exact old text beyond the pins listed: report it before changing.
