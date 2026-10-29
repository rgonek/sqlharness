# Task 3 scoped re-review, round 1

Reviewed `review-3010a1e7..f47e903c.diff` and the appended Task 3 evidence. Product fix: `a44b2661`; report update: `f47e903c`. Scope was the original expansion-budget P2 and regressions introduced by its correction.

**Original P2: ADDRESSED. Specification: PASS. Quality: PASS for this scoped correction. No new actionable finding.**

The formatter Proxy now counts original SQL plus every fully escaped literal occurrence, and throws before returning a replacement that would exceed 100,000 characters. This bounds the repeated-value expansion before the formatter can materialize the previously oversized preview. Counting the original placeholders as well as replacements is conservative and safe. The final rendered-length check additionally rejects formatting overhead; it is not being relied upon to prevent repeated-value allocation. Failure continues to return the exact original SQL and the existing explanation.

Independent safe probes of the current helper confirmed:

- The original reproduction (1,919 SQL characters, 10,000-character `nvarchar(max)` value, 101 references) now returns exactly the 1,919-character original with a fallback reason.
- The same query with a 10,000-character apostrophe-containing value also returns exact original SQL, verifying that escaped literal size is included.
- Small repeated `O'Brien` text and bigint `9007199254740993` still inline twice with correct escaping and no numeric precision loss.

The added helper regression covers the escaped expansion and successful small repeats. The added dialog regression verifies disabled inline mode, selected As sent, exact original SQL, preserved request data and usable Deny/Approve with the existing delay. The production diff does not touch requester provenance, authorization, execution payloads or lifecycle code.

The implementation report records fresh 18/18 focused UI tests plus typecheck/lint/build. Those commands were not repeated here; independent evidence in this round is the source review and bounded helper probes above. No full suite, giant allocation, live decision, DB write, source change, index change or HEAD change was performed. Parent browser/final-branch acceptance remains separate.
