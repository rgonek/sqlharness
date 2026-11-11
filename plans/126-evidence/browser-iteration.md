# Browser iteration after final review

Product 50474800, initial actual-backend browser run: 67/72 checks; five performance series pass. The v2 seed omitted the mandatory sha256: prefix; SQL lookup was rejected by the real endpoint, so that run is not final SQL acceptance. Generator corrected and a new v3 home created without overwriting the old journal.

V3 performance (20 samples each, same frozen dataset): search p95 75ms, facet112.4ms, expansion105.8ms, A/B219.8ms, case182.2ms; maximum long task125ms. All frozen limits pass. Functional iteration still had comparison-theme timeouts, a real SQL-comment contrast failure (4.43:1 on deleted-text background), clipboard newline mismatch and a real approval focus-return failure. Exact raw evidence retained in browser/final-v3.

Fixes: dedicated comment token with higher contrast, used by Monaco and highlight.js. Approval dialog explicitly restores focus to the pending request's remounted Review action; decision/policy logic unchanged. New native keyboard regression failed with focus on body, then passed after correction. The first experimental hidden-toast solution stalled focused tests and was discarded. Final focused Approval + Monaco suite37/37 passes. Browser acceptance still pending new build.

Harness corrections: wait for menu closure and focus completion; record exact Clipboard API argument, separately allow Windows CRLF in native clipboard readback. These are test conditions, not product workarounds; original SQL must still reach Clipboard API byte-for-byte.

Screenshot inspection also found the pinned first column and A/B actions consumed nearly the full 390px viewport. Narrow-screen identity is now constrained to96-116px with wrapped labels, leaving room for horizontally scrolled metrics; a mobile caption explains that scroll. This layout change superseded the second full gate: build PASS, UI checks intentionally interrupted before result, no full gate claim. Final gate remains required after visual checks settle.
