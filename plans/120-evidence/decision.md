# Plan 120 decision — defer a supported suite feature

## Decision

Choose **C: no new supported feature now**. Defer option B, a CLI manifest command. Keep option A, a checked-in list of existing commands, available as an operator-owned workaround where the current commands preserve the needed input semantics. Reconsider B only after the demand and design gates below are met.

This is a research-complete disposition, not implementation approval. The study shows that an explicit manifest can make a small, ordered set of comparisons reproducible in a mock: exact input-byte pins, explicit per-entry targets and policies, ordered dispatch, retained completed outcomes, and stop-on-unknown behavior are feasible. It does not show that this capability solves a recurring operator problem. The 60-versus-26 placement count is a synthetic ergonomics estimate, not observed time saved or proof that users need a product feature.

## Need supported and evidence limits

The hypothesized need is to repeat a multi-subject comparison without manually rebuilding each invocation or losing which input bytes and outcome belong to each comparison. Existing compare commands and MCP named-set inputs already perform individual comparisons; the possible gap is organizing several explicit comparisons together.

The mock gives evidence about safe sequencing and evidence retention, not user demand, target authorization, real operation/artifact identifiers, database snapshot identity, production path-race behavior, or real comparison behavior. It exercised 26 synthetic cases and 17 in-memory dispatches with zero database and external-process calls. The OS did not permit creation of a test symlink; escape rejection was simulated and labeled as such. Do not treat this as an executed filesystem proof.

## Alternatives

| Alternative | Disposition | Cost and tradeoff |
|---|---|---|
| **A. External command list/helper** | Keep as the current low-cost option when existing command forms preserve the workload. A reviewed script may hold explicit invocations, but it owns ordering and partial-run bookkeeping. | Very low product cost; the operator maintains the list and may duplicate input literals. It cannot claim the semantics of an MCP named-set operation if it expands that operation into separate CLI calls. Do not add a repository helper until a real workflow shows the plain list is insufficient. |
| **B. Supported CLI manifest surface** | Defer. Feasibility is not enough to justify a maintained interface. | Coarse estimate: medium-to-large implementation, roughly 2–4 engineer-weeks for a narrowly supported CLI/Core path, plus ongoing compatibility and security maintenance. This is a planning range, not measured effort. The cost includes manifest/schema validation, safe file resolution and hash-race handling, dispatch and partial-result identities, CLI help/docs, fixtures and focused tests, and integration with existing parameter resolvers. Extending parity to MCP named sets or additional input kinds would add scope and maintenance. |
| **C. No new feature** | Choose now. Continue using existing individual compare operations and current MCP named-set support; record inputs and outcomes through existing artifacts. | No new product maintenance. It leaves cross-comparison organization manual, which is acceptable until recurring demand and the limits of A are demonstrated. |

The estimate for B excludes a universal suite verdict, retries, resume/replay, mutations, target inference, deployment behavior, a scheduler, and changes to Plan 036. Those are outside the candidate and would require a separate decision. A manifest hash would pin file bytes only; it would not prove a database snapshot or validate/reuse prior evidence.

## Explicit gates to reopen B

Reopen the feature decision only when all of these are documented with a real, non-sensitive workflow:

1. At least two independent operator workflows, or one named workflow repeated at least three times over a month, each has three or more comparison subjects.
2. The operator records the actual manual reconstruction and evidence-linking steps for those runs and explains why a checked-in list of existing commands (A) does not adequately solve them. Do not use the synthetic placement count as demand evidence.
3. The requested inputs are enumerated by current CLI/Core/MCP shape. Any CLI/MCP parity gap is explicitly accepted or removed from scope; no input shape is converted to another while claiming semantic parity.
4. A separately reviewed implementation design resolves rooted-file policy, canonical path and symlink/reparse handling, hash-to-read races, existing reader/resolver reuse, operation/artifact identity, completed-negative retention, and stop behavior without retries or inferred targets.
5. The operator accepts that the surface organizes fresh explicit comparisons only: it cannot resume from old results, reuse evidence, establish a shared database snapshot, grant target access, or issue persistent writes.

If the evidence does not satisfy these gates, retain C. A local recurring script may still be appropriate under A without creating a supported product surface.

## Supported and unsupported boundary in this study

The simulator covered fixed typed scalar input and MCP named parameter-set files in synthetic data. It intentionally did **not** establish full current reader grammar parity (BOM, comments, trailing commas or all Core type-resolution rules). The following remain unsupported or deferred: CLI compare named sets and parameter-set files, CLI/MCP matrix equivalence, fixed-parameter-file parity, TVP/list inputs, mixed input forms, execution against real targets, symlink/reparse filesystem proof, and true concurrent file-race behavior. The synthetic boundary-date, duplicate-sensitive negative result, stale digest, and partial-stop examples do not expand those limits.

No accepted feature means there is no future product file map or product test plan in this packet. If B is later selected, create a new scoped implementation plan after the gates pass; do not treat this study design as authorization or a complete implementation contract.

## Evidence and verification boundary

- [Input and contract inventory](inventory.md) maps proposed fields to their current authorities.
- [Dispatch study design](design.md), [case inventory](cases.json), [evaluator](evaluate.ps1), [independent verifier](verify.ps1), and [synthetic results](results.json) define and record the bounded mock.
- [Historical task reports and reviews](logs/README.md) preserve the preceding task evidence. Their earlier case counts describe earlier revisions; the final study count above is 26 cases and 17 mock dispatches.
- Scoped study checks validate generated synthetic evidence and documents only. No product build, SQLHarness invocation, database access, runtime installation, CI dispatch, or live acceptance is claimed.
