# Measure observations in the Optimization view — design

Date: 2026-10-17. Plan: 087. Depends on plan 079 (DONE: conditions model, fingerprints, measure per-case sessions)
and plan 080 (Optimization section, in progress). Plan 084 follows this plan.

## Intent

Agents often `measure` a version (for example the original v0) and later `compare` other versions, or optimize with
`measure` only. Today `measure` carries no `subject`/`scope`, so the Optimization section (080), which reads only
`compare`, ignores those measurements and a measure-only session gets no Optimization section at all.

## Goals

1. **`measure` accepts optional `subject` and `scope`** (MCP arguments and CLI `--subject`/`--scope`), validated by
   `CompareContext.Parse` (same slug pattern, `whole` scope). They are optional for `measure` regardless of
   `compare.requireContext` (measure also serves non-optimization benchmarking); the parameter description says to
   pass them when the measurement belongs to an optimization subject. Journaled in the existing `compare_subject` /
   `compare_scope` columns with the measured SQL's hash in `sql_hash`.
2. **Version history includes measure.** Version numbering per (context, subject, scope) counts measured SQL hashes
   together with compare baseline/candidate hashes, chronologically by first appearance.
3. **Measure as observations.** A measure run is a one-sided observation of one version: per-case logical reads,
   scans, elapsed/CPU medians and ranges, under its conditions fingerprint (079). It sits side by side with other
   observations of the same group (same operation-level fingerprint), labelled `different run`.
4. **Panel semantics (amends 080 behavior, not its code ownership):**
   - Pair basis: `measured together` only for two sides of one compare; any pair involving a measure observation is
     `different runs, same conditions` (or `conditions differ` when fingerprints differ).
   - Equivalence: a version observed only by measure has `results not compared`; equivalence chains run through
     compares only.
   - Default pair: unchanged 080 rule when the group has at least one compare; in a group with measures only,
     A = earliest version, B = newest version, panel labelled `results not compared`.
   - Better/worse, Mixed and worst case use the same logical-reads rule as 080 (it needs no equivalence).
   - Version table outcome column for a measure-only version: `measured only`.
5. **Section visibility:** the Optimization section appears when the session has a compare **or** a measure with a
   subject; measures without a subject are not listed (they have no version identity).
6. **Plan 084** (agent sessions, frozen labels) must treat measure observations like compare ones; add the dependency.

## Calls settled after the builder check (my calls)

1. **Measurement fingerprint.** 079's operation fingerprint includes `execution.comparisonMode` (null for measure),
   so measure and compare could never share a group. Add a **measurement fingerprint** (operation-level and per case)
   computed by `ActivityJournal` with the same salt over the 079 payload **without** `comparisonMode` and
   `compareNullability` (they affect only the equivalence check). Version grouping and case pairing use it; cases pair
   by measurement case fingerprint, not by case id (matrix ids `0,1` vs set names differ). New journal step per
   `FirstRetainedVersion` rules; existing rows without it fall back to the 079 fingerprint.
2. **Observation choice.** For a selected pair, prefer one compare containing both versions (`measured together`);
   otherwise the newest observation of each version in the group (`different runs, same conditions`). The version
   table row shows the newest observation.
3. **Default pair.** A = earliest version in the group; B = the 080 rule (newest version linked by an equivalent
   compare chain) when one exists, else the newest version, labelled `results not compared`. The panel is never empty
   when the group has two or more versions.
4. **Order.** Plan 087 is written against 080's merged code (drift check); order 080 → 087 → 082 and 084 (both
   change the comparer/history after this plan).
5. **Sessions list:** the "contains compare" filter becomes "contains optimization" (compare, or measure with a
   subject); the Optimization column includes measure-only subjects.

## Non-goals

- Result equivalence for measure (measure does not compare rows).
- Inferring subject/scope for measures that lack them.

## Anti-goals

- Presenting a measure-only pair as equivalent or as measured together.
- Mixing observations across different conditions fingerprints.

## Constraints

- MCP: `subject`/`scope` members on `measure` always present (optional); keep tools/list within 32 KiB; update the
  measure description once (no duplication in compare's description).
- Journal: no schema change expected (columns exist); if one is needed, follow `FirstRetainedVersion` (14) rules.
- Lands after 080; 084 depends on this plan. No auto-formatting; `just verify`.
