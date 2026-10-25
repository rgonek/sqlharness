# Plan 118 local main integration

- Merge: `907e30ec845ccc39aeaccae3667a6f59fb96d1a8`; reviewed branch tip: `52fcc30e`.
- Main before merge: `7c1b2fbf`; source, tests and shared scripts unchanged.
- Merged study evidence and executable bytes equal the reviewed branch.
- Integrated-tree `pwsh -NoProfile -File plans/118-evidence/verify.ps1`: exit 0; 34 scenarios.
- Results bytes restored after verifier to preserve recorded executable revision.
- Full-range `git diff --check`: passed.
- Product gate not run: offline study integration only. No push.
- Pre-existing README/index edits temporarily stashed; restored after integration bookkeeping.

```text
cases=34 output=D:\Dev\sqlharness\plans\118-evidence\results.json
VERIFIED: 34 named scenarios; exact oracle values, basis preservation, absent-property states, fingerprint semantics, pairing non-disclosure, omissions, and UTF-8 limits passed.
```
