# NVDA acceptance after desktop unlock

Run: 2026-11-11T19:10:34.507Z to 2026-11-11T19:12:48.617Z. Native NVDA2026.3.1 portable; actual Windows desktop and dedicated Chrome profile. Final product85c75cb2. Source diff SHA-256 is the empty-diff hash. All three served entry/dynamic CSS asset hashes match the current local Release build.

**PASS:17 steps, no harness error.** [Recorded focus and NVDA speech](nvda-85c75cb2/screenreader.json). The operator explicitly reported the desktop unlocked before this run.

| Workflow | Verified result |
|---|---|
| Skip navigation | NVDA announces the skip link; native keyboard activation focuses main, then Tab reaches Search activity. |
| A/B | Announces A v48/B v49 radio names and checked states; native Left changes B to v48 and URL agrees. |
| Cases | Announces @CustomerId=2; native Space selects it and URL resolves synthetic-case-1. |
| Outcome dialog | Opens and announces Case outcomes; native browse-table navigation reads six headers, case data, recorded policy Fail and elapsed-regressed. Escape restores Policy focus. |
| SQL | Announces Baseline SQL as read-only multiline editor and reads original SELECT text. Tab reaches Candidate SQL; Shift+Tab returns to Baseline SQL. |
| Copy | Native Enter activates Copy baseline SQL; operation succeeds, NVDA announces Copied baseline SQL and keyboard focus remains on the copy button. |

## Method and limits

The harness uses an actual NVDA speech pipeline with the silence synthesizer. No audible listening, braille or blind-user usability study is claimed. Some controls receive explicit focus as test setup; the complete application is not claimed to have been traversed solely by keyboard. The recorded actions, keyboard transitions and reader output cover the frozen representative workflows alongside the existing browser keyboard/axe/zoom checks.

Every native input validates the dedicated browser profile and its foreground window. Secure Desktop is rejected. The portable NVDA instance is stopped in finally; no machine installation/startup change, SQL workload or mutation decision occurred. Raw NVDA logs remain outside the repository; only audited application speech/focus is retained.

## Harness diagnostics

Initial attempts stopped on foreground ownership or assumed a first-Tab position different from NVDA virtual-cursor initialization. A literal selector also had an encoding mismatch. Those initial corrections changed the harness. Reviewing run8 then exposed a product issue: temporary native disabling of CopyButton moved focus to BODY. Commit85c75cb2 uses Base UI focusableWhenDisabled for the pending state while still blocking duplicate activation and preserving disabled appearance. Explicitly unavailable actions stay disabled. The helper now validates the profile/window, transfers foreground when needed, and sends synchronous native keys. The final run explicitly asserts case/pair URL state, main/dialog/editor focus and spoken policy failure/copy success.

Attempt folders nvda-unlocked through nvda-unlocked7 are retained as diagnostic evidence. Run7 was preliminary; run8 verified policy reading but exposed normal-copy focus loss. The accepted final run is nvda-85c75cb2, which also strictly asserts retained copy focus. The previous Secure Desktop blocker is resolved.

The copy fix observed the intended failing regression, then passed11 focused component tests and [five real-browser checks](../copy-focus-85c75cb2/copy-focus.json), including full6000-line original SQL, manual fallback and Tab/Shift+Tab. The final Windows gate must be read from [verification](../../verification.md); the earlier5f05ca07 gate is historical.
