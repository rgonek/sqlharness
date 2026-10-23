# Whole-branch review — Plan 107

Reviewed range: `6d543e50..eb7a9145`, using the supplied commit/stat/contextual-diff package, current changed tests, diagnostic evidence, binding constraints and recovery ledger. Read-only review; no tests rerun and no production files changed.

## Strengths

- The final branch honors the diagnostic contract: production behavior is untouched; the only executable changes are offline characterization tests. The source repair is a separate, narrow follow-up with desired behavior and safety constraints.
- The real-SDK MCP matrix independently sends all 12 synthetic shapes, checks meaningful error codes/messages, and enforces zero connection attempts through a counting forbidden session factory. Singleton, multi-value, empty, multiple-set, setup reuse and explicit caller parameters are represented for both forms.
- Desired-success RED evidence remains visible beside accurately named green rejection characterization. Core's combined named-set operation stops at its first rejection; the separate MCP requests provide the individual shape evidence rather than claiming every Core set completed.
- Runtime identity is responsibly separated: the on-disk older candidate does not prove the historical loaded process; the known-source test logs directly identify loaded Core/MCP assemblies, locations and MVIDs. Mechanistic and historical causal uncertainty is explicit.
- Archived nonempty Windows logs support the stated gate results on `19035a07`: Core 4657 passed/11 skipped, MCP 508 passed/4 skipped, UI 372 passed. Subsequent changes are documentation/evidence only. No reason was found to repeat the expensive gate.
- No credentials, production target requests, live database actions, approval bypasses, limit increases, install or restart changes appear in the reviewed branch.

## Issues

### Critical (must fix)

None.

### Important (should fix)

None.

### Minor (nice to have)

1. **Supplied-collision summary overstates what the bare-form result proves.** `plans/107-evidence/task-3-diagnosis.md:7` says both supplied-collision requests preserve the expected caller-collision outcome. Both do reject, but the bare form fails with `ParseError`, before a collision-specific outcome is established. The ordinary parenthesized request also collides, so the MCP supplied variant alone is not differential proof of caller-collision handling. The explicit Core caller-scalar test and retained expander coverage provide useful separate protection. Describe the matrix as “both supplied-parameter requests rejected; bare-form collision detection remains unproven by this result,” and point to the specific collision test. This does not undermine the confirmed ordinary-path defect or the diagnostic completion.

2. **Qualify the zero-warning build summary.** `plans/107-evidence/task-3-diagnosis.md:33` reports “build passed with 0 warnings/errors.” That matches the .NET summary, but `plans/107-evidence/windows-gate-20261023-task3/build.log:27` contains a Vite chunk-size advisory. Node DEP0190 is already mentioned separately. Say “.NET build summary: 0 warnings/errors; Vite chunk advisory and Node DEP0190 retained in logs.” This is evidence-summary precision, not a gate failure.

Both are the deferred Minors already recorded in the ledger; no new blocking findings.

## Recommendations

Keep the two wording corrections small if taken. In the separately authorized source repair, convert expected-success evidence into live regression assertions and preserve explicit caller-collision checks and zero-connection assertions. Do not interpret the green characterization suite as product acceptance.

## Declined to judge

- Correctness of the eventual production list-classification repair: explicitly excluded from this diagnostic branch; no repair exists here to review.
- Exact parser/collision mechanism: current evidence establishes the rejection path, not its implementation cause; follow-up requires source confirmation.
- Historical process/build attribution and client payload equivalence: exact historical managed assembly/payload evidence is unavailable and correctly left unresolved.
- Linux, live SQL, installed-server acceptance, repeated-run identities and measured model-token savings: not exercised, not authorized/required for this offline scope, and no passing claim is made.
- Artifact compatibility, clipping, continuation and engine-specific-field behavior: no affected production or artifact projection changes; nonapplicability is explicitly assessed in the diagnosis.
- Main integration, push, installation and restart readiness: outside authorization; this verdict concerns the reviewed local branch only.

## Assessment

**Ready to merge? Yes, for the completed diagnostic scope, with two nonblocking documentation Minors.**

The branch supplies the required bounded offline reproduction, truthful known-source classification, explicit runtime uncertainty and separate repair plan while preserving security boundaries. This is approval of diagnostic delivery, not approval to merge or a claim that the list workflow is fixed.
