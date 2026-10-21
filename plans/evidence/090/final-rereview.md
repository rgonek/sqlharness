# Plan 090 final scoped re-review

Range: `902f125..f4828c191e11147435c4e5e0bda0eb40de551020`. Reviewed documentation/archive fixes only; no product re-review, tests or gate rerun.

## Prior findings

- **ADDRESSED** — `plans/090-verification-evidence.md:21` now separates three defect regressions from two corrected over-specific assertions.
- **ADDRESSED** — `plans/090-matcher-title-backfill-retention.md:54` now requires in-flight batch survivor evidence and makes persistent staged survivor coverage conditional on an applicable writer.

## New breakage in this fix wave

### Important — promised durable raw logs are absent from the commit

`plans/evidence/090/README.md:3-5` and `plans/090-verification-evidence.md:29` claim the archive preserves raw focused/formatter/gate logs without relying on ignored scratch artifacts. However, `git ls-tree -r --name-only f4828c1 plans/evidence/090` lists eight Markdown files and no logs. The logs exist locally, but `git check-ignore -v plans/evidence/090/gate-console.log` reports `.gitignore:101:*.log`. Consequently another checkout cannot inspect the promised direct exit evidence, and the archive does not fulfill the documented durable-evidence claim.

Fix by explicitly tracking the intended compact text logs (for example, force-add these exact evidence files, or rename them to tracked text extensions and update the archive references). No tests or gate rerun are needed; preserve the existing outputs.

No new Critical or Minor finding. Historical reports retain historical wording and are clearly archived records; the canonical evidence corrects it.

## Declined to judge

None within the scoped documentation/archive fix wave. Product behavior and previously proven Windows/focused checks were not reopened.

## Assessment

**Ready to merge? With fixes.** Both prior Minors are addressed, but the newly promised raw-log archive is not committed. The original product readiness verdict remains unchanged; this finding concerns durable documentation evidence only.
