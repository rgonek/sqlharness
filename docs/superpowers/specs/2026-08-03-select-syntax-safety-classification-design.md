# Complete SELECT Syntax Safety Classification

**Date:** 2026-08-03

## Problem

SQLHarness currently treats every ScriptDom AST fragment as forbidden unless its
concrete runtime type appears in `AllowedFragmentTypes`. This makes ordinary,
read-only T-SQL fail closed for the wrong reason. A query using a CTE, table or
index hints, `RIGHT(...)`, or an optimizer hint can be rejected as
`UnsupportedStatement` even though its top-level operation is a `SELECT` and it
does not perform a write or another prohibited action.

Adding the four currently reported fragment types would only repair those exact
examples. Future valid `SELECT` constructs, and constructs newly represented by
a later ScriptDom version, would continue to require one-off allowlist changes.

## Contract

For `query`, `watch`, `snapshot`, `measure`, and each side of `compare`,
SQLHarness accepts every T-SQL construct that ScriptDom can parse inside a
top-level `SelectStatement`, unless an independent safety rule identifies a
prohibited behavior.

The classifier remains fail closed at the statement and behavior boundaries.
Unknown or unsupported top-level statements are rejected. This design does not
make arbitrary T-SQL read-only merely because it contains a nested `SELECT`.

## Safety model

Classification has three independent stages:

1. Parse the complete batch with ScriptDom. Parse errors remain rejected.
2. Classify every top-level statement according to the command's existing
   policy. Ordinary read-only execution permits `SelectStatement`; setup and
   explicitly approved mutation paths retain their current, narrower statement
   rules.
3. Inspect the full AST for prohibited behavior regardless of which safe
   top-level statement contains it.

The behavioral inspection continues to reject at least:

- cross-database object references;
- external access such as `OPENROWSET`, `OPENQUERY`, ad hoc sources, and bulk
  rowsets;
- stateful sequence access through `NEXT VALUE FOR`;
- `INSERT ... EXEC` sources;
- persistent or global-temp `SELECT INTO` where the active command policy does
  not permit it;
- persistent `OUTPUT INTO` in comparison setup;
- persistent writes without the existing exact, single-use mutation approval.

The nested AST fragment allowlist is removed from the decision path. CTEs,
scalar functions such as `RIGHT`, table/index hints, optimizer hints, windowing,
and other parsed `SELECT` syntax are therefore accepted without registering
each concrete ScriptDom node type.

## Components and data flow

`SqlSafetyClassifier` remains the single safety boundary shared by all affected
commands. No command-specific parser or fallback path is added.

The existing statement classification methods retain responsibility for
deciding whether a top-level operation is valid for `SqlUsage.Query` or
`SqlUsage.CompareSetup`. `SafetyInspectionVisitor` retains responsibility for
detecting forbidden behavior anywhere in the parsed tree. The reflective
`CollectUnsupportedSyntax` fragment traversal and `AllowedFragmentTypes` cease
to gate execution.

Rejection messages continue to avoid echoing SQL text or literal values.
Unsupported top-level statements may still report their statement type, but a
previously unseen nested fragment type is not an error and is not reported.

## Verification

Regression tests exercise the shared classifier with a representative query
containing all reported failures together:

- a CTE;
- an index/table hint;
- `RIGHT(...)`;
- an `OPTION (...)` optimizer hint.

Additional tests cover representative read-only syntax likely to expose the
same architectural issue rather than merely the four named node types.

Security regression tests prove that removing nested-fragment allowlisting does
not permit prohibited behavior, including `EXEC`, cross-database references,
external rowsets, stateful sequence access, persistent `SELECT INTO`, and
unapproved writes. Existing setup and mutation tests remain part of the gate.

The implementation uses test-driven development: the compatibility regression
must fail with the current classifier before production code changes. After the
targeted tests pass, the full test suite and repository formatting/build gates
must pass before completion is claimed.

## Non-goals

- Expanding the set of allowed top-level statement kinds.
- Weakening mutation confirmation or target locking.
- Adding a `sqlcmd` fallback.
- Proving domain equivalence of two queries.
- Maintaining an exhaustive catalog of ScriptDom fragment types.
