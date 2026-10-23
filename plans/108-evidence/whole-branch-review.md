# Plan 108 whole-branch review

Range: `6d543e50..32ff3e5b`; product/test revision `753193a9`. Read-only review of the full diff, embedded contract, task reports/reviews, and saved focused evidence. The independent branch formatting check and full Windows gate were running during this review, so their results are not included in this verdict.

## Strengths

- The shared reader resolves an empty variant only after manifest and owner validation (`ArtifactReader.cs:225-243, 452-480`). Compare still rejects omitted or empty variants with Safety classification; explicit casing and whitespace remain invalid.
- Default and explicit `query` use the same selector path, preserving statement, parameter set, repetition, and returned identity. The reader tests cover these selectors and invalid values; the in-memory MCP test exercises actual omission through the served tool protocol.
- Both MCP tool descriptions and the artifact contract describe the new selection rule. No saved-artifact schema, safety boundary, credential handling, or response budget changed.

## Issues

### Critical

None.

### Important

None.

### Minor

1. `src/SqlHarness.Cli/Commands/ArtifactCommand.cs:33` and `README.md:379`: CLI help still lists the three variants without telling users that measure and measure-set default to `query`, and the README says to select `--variant`. Since the same reader now serves CLI and MCP, these human-facing surfaces leave CLI users with the old workflow. Update their wording to match the new behavior, including compare's required explicit side.

## Recommendations

Resolve the CLI/README wording before marking the plan complete. Finish the pending branch format and Windows gate, then record their direct results, tested revision, final checklist/status, and index entry. The saved focused runs report 87 Core/CLI and 46 MCP tests passed; this review did not rerun them.

## Declined to judge

- Existing measure-set behavior that picks the first sorted parameter set when none is specified: unchanged and outside Plan 108's variant contract.
- Whether a built or deployed MCP binary uses this branch: no installation or live target was in scope.
- Linux and live UAT behavior: neither was run or claimed in the evidence.

## Assessment

**Ready to merge? With fixes.** Product behavior meets the Plan 108 contract. The CLI/README documentation inconsistency is minor but should be fixed, and the pending required gate/evidence must finish before the branch can be declared ready.
