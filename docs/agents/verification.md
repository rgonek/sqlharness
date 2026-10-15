# Verification

## Implementation

Run `just verify`; it must exit successfully on the final product tree before declaring implementation complete. Inspect the failed stage's log before fixing it. For formatting failures, use `just fix` and rerun the gate. Command help and the justfile describe available options.

`just verify-linux` still exists and runs the same stages inside WSL on a case-sensitive ext4 clone. It is not part of the completion criteria while CI runs on the local machine; restore it there when CI moves to GitHub Actions. It needs `pwsh ./scripts/setup-linux-gate.ps1` provisioned once, and it checks committed code, so commit before running it.

Report the tested revision and actual results; distinguish feature-branch evidence from verification of an integrated tree. Repeat a completed gate only for changed product code or a concrete unresolved risk, subject to the user's instructions.

## Documentation only

Check the diff, links, skill frontmatter and affected documentation contracts/examples. The gate is unnecessary when executable behavior is unchanged. Preserve unrelated local work and commit only the requested changes.