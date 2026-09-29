# 002/T3 — File-identity assessment for `McpInputReader`

Scope: byte limit + reparse/link refusal retention, swap test, verdict on
whether `length`/`mtime` checks are accidental-change detection or real file
identity. No behavior change in this task (default: no `src` edits).

## 1. Current mechanism

`src/SqlHarness.Mcp/McpInputReader.cs` (line numbers at BASE `8654c233`):

- Admission: fully-qualified path only (`Path.IsPathFullyQualified`, lines
  138–140), UNC rejected (line 132), ADS colon in file name rejected (line
  153), must be under an admitted root
  (`McpInputRoots.IsUnderAnyRoot`, line 155), link/reparse chain over the file
  and every parent directory rejected up front (`IsLinkOrReparseChain`, lines
  157, 240–251; fail-closed on I/O errors, lines 262–265).
- Pre-read size gate: `new FileInfo(full).Length > maxBytes` rejects before
  opening (lines 163–164). Budgets (`McpLimits.cs`): SQL/plan 16 MiB, inline
  1 MiB, parameter-set 64 KiB.
- Single open (line 168): `FileMode.Open`, `FileAccess.Read`,
  `FileShare.Read | FileShare.Delete` (no `FileShare.Write`), async +
  sequential scan. `FileShare.Delete` lets an operator rotate files by rename
  while a read holds the old entry; the post-read snapshot then rejects the
  replaced file instead of mixing old and new bytes (comment, lines 165–167).
- Bounded read (lines 202–218): at most `maxBytes + 1` bytes are pulled from
  the open stream, then oversize is rejected — the on-disk file can grow past
  the bound mid-read without the reader consuming it unboundedly.
- Snapshot pair: `before = Snapshot(full)` (line 181) is taken after the open,
  `after = Snapshot(full)` (line 184) after the bounded read. `Snapshot`
  (lines 268–281) records `(info.Length, info.LastWriteTimeUtc,
  IsLinkOrReparse(full))`, failing closed to `(-1, DateTime.MinValue, true)`
  when the entry is missing or unreadable.
- Post-read re-validation (line 185): reject with `"The input file changed
  while it was read."` when `!before.Equals(after)`, or the path is no longer
  under an admitted root, or any chain element became a link/reparse point.
- Test seam: internal `afterOpen` callback (lines 105, 115, 183) fires between
  the read and the `after` snapshot so swap tests are deterministic — no
  timing involved.

## 2. What it detects

- Accidental replacement (operator rotating files, editor save = delete +
  recreate with different length): the `after` snapshot differs in
  length/mtime → rejected. Covered by `Replace_during_read_is_rejected`
  (`tests/SqlHarness.Mcp.Tests/McpInputReaderTests.cs`, lines 169–186).
- Mid-read link swap: if the path (or any parent directory) becomes a
  symlink/reparse point between the snapshots, the `IsLink` flag or the chain
  re-check trips → rejected. Note the flag is only as fresh as the two
  snapshot instants.
- Root escape after open: the string-level `IsUnderAnyRoot` re-check (line
  185) catches a path that no longer resolves under an admitted root (e.g.
  parent renamed). It does not track the open handle — see section 3.
- Pre-existing escapes and oversize: symlinked file or parent, ADS, UNC,
  traversal, and over-budget files are refused before any byte is read
  (covered by T1 tests `Symlink_and_reparse_escapes_are_rejected`,
  `Nested_relative_symlink_inside_root_is_rejected`, `Oversized_*`).

## 3. What it does NOT give

No protection against a deliberate local race (TOCTOU). Concretely:

- The snapshots observe the *path*, not the *open handle*. Bytes are read
  from the stream opened at line 168; identity is re-checked via fresh
  `FileInfo` lookups on the path. An adversary that writes through the same
  open file description, or that replaces the entry with byte-identical length
  and an indistinguishable `LastWriteTimeUtc`, passes the equality check.
- `LastWriteTimeUtc` resolution is filesystem- and platform-dependent; two
  writes inside one clock tick can share a timestamp. Length/mtime equality
  is therefore evidence of *no observed change*, not proof of *same file*.
- The reader deliberately opens without `FileShare.Write`, which makes
  casual concurrent writers fail with a sharing violation instead of
  silently corrupting the read — but share-mode denial is an OS courtesy,
  not an identity guarantee, and its enforcement differs between
  Windows and Linux (.NET emulates share semantics on Unix).
- Parent-directory swaps that preserve the path string and contain no
  link/reparse element (e.g. atomic rename of a real directory over the
  parent) are invisible to both the string root re-check and the chain walk.

Length/mtime comparison is therefore **accidental-change detection, not
cryptographic or handle-based identity**. It answers "did the directory entry
observably change during the read" — nothing stronger.

## 4. Verdict

Length/mtime = exclusively accidental-change detection. That is sufficient
for the MCP threat model: a cooperative operator rotating input files, not an
active local adversary racing the reader. No race protection is claimed.

Full identity would require opening relative to a held root handle (e.g.
`File.OpenHandle` + handle-relative open / `RandomAccess`, comparing file IDs
rather than path metadata) — that is a SEPARATE hardening project, explicitly
out of scope via the plan's stop condition (no new system APIs in this fix).
Do not advertise race protection without such a proof.

## 5. Proof status

- Deterministic swap proof exists: `Replace_during_read_is_rejected` uses the
  `afterOpen` callback (delete + recreate with different length) — no sleeps,
  no timing gate; must stay green.
- No additional swap variant was added in T3. Candidates assessed and
  rejected as non-deterministic:
  - *Same-length overwrite via a second handle*: the reader holds the file
    without `FileShare.Write`, so a concurrent writer fails with a sharing
    violation (or platform-emulated equivalent) instead of reaching the
    snapshot comparison — the outcome tests OS share enforcement, not the
    reader, and differs between Windows and Linux. Not a valid variant.
  - *Same-length delete + recreate* (e.g. `"SELECT 1;"` → `"SELECT 2;"`):
    detection would rest entirely on `LastWriteTimeUtc` differing between two
    writes in the same callback. Timestamp granularity is uncontracted, so a
    shared tick would make the test pass the reader while failing the
    assertion — a timing-flaky test by definition. Excluded per the brief's
    no-flaky-tests rule.
  - *Parent-directory swap mid-read*: needs link-creation privilege (not
    available deterministically on all Windows machines) or relies on
    uncontracted rename semantics; the string-level re-check cannot observe a
    handle-preserving swap anyway. No deterministic assertion exists.
- No proof against an active adversary is offered — stated explicitly, not
  implied. Limits (`Oversized_*`) and link/reparse refusals
  (`Symlink_and_reparse_escapes_are_rejected`,
  `Nested_relative_symlink_inside_root_is_rejected`) are untouched by T3: no
  `src` changes, no normalization/`Scope` changes, no new dependencies.
