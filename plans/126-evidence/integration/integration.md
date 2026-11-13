# Plan126 integration with current main

Incoming quality branch:c062822a. Current main/origin main:85d1c95e. Base:a274b0ec. Main independently integrated Plan125 at ef580dc; its contributor completeness, statement availability, page-server IO and regression tests must be preserved alongside quality/navigation/accessibility work.

Resolution is complete; final combined verification is pending. The merge was prepared in the existing quality worktree. The untracked main-worktree design proposal is preserved at `D:/temp/sqlharness-quality-main-20261113/untracked-dashboard-quality-design.md`, SHA256 `BB100F32CA6B59EE75D38169531B5187ADCC8EE660AC8E45E389306C7DFCCB44`. User explicitly authorized merge and push; no force push, live SQL or installed dashboard changes are part of integration.

## Resolution checks

- Independent integration [review](review.md): one P2 fixed with an observed red/green regression (9/9 green).
- Focused C# evidence/reader/agent optimization suite: 304 passed, zero failures, exit 0; see logs/backend-focused.log. Initial compile exposed two merged helper calls missing the seed argument; both were corrected.
- UI typecheck and lint exit 0 (two existing hook-dependency warnings). Focused UI suite: 147/148 initially, then the corrected VersionTable file 16/16; the final full gate will independently check all UI tests.
- The approximate outcome regression explicitly preserves raw reads 100/50 while withholding an IO verdict across different conditions.

Full Windows verification and actual synthetic browser checks will run from the integrated main checkout before push. Previous quality scores and native accessibility evidence remain tied to their recorded revisions, not relabeled as new integration evidence.
