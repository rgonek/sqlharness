# Verification

## Implementation

Run `just verify`, then `just verify-linux`; both must exit successfully on the final product tree before declaring implementation complete. Run one gate at a time. The Linux gate uses the provisioned WSL ext4 clone and checks committed code, so commit implementation changes before that gate.

If the Linux clone is not provisioned, use `pwsh ./scripts/setup-linux-gate.ps1`. Inspect the failed stage's log before fixing it. For formatting failures, use `just fix` and rerun the affected gate. Command help and the justfile describe available options.

Report the tested revision and actual results; distinguish feature-branch evidence from verification of an integrated tree. Repeat completed gates only for changed product code or a concrete unresolved risk, subject to the user's instructions.

## Documentation only

Check the diff, links, skill frontmatter and affected documentation contracts/examples. Full gates are unnecessary when executable behavior is unchanged. Preserve unrelated local work and commit only the requested changes.
