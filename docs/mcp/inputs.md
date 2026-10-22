# MCP inputs

## Input roots and file inputs

Inline input works without filesystem access. File input requires an absolute path lexically under an `--input-root` directory frozen at startup. UNC and network paths are rejected, and each file is opened once with a bounded read. The reader does not resolve links or reparse points, reject alternate data streams, or detect same-user replacement while reading: the roots scope caller-supplied paths, while the same OS user can read or change filesystem entries directly.

`sqlharness_capabilities` discloses the configured input roots in both modes, so a caller can find a permitted location without reading client configuration:

- `inputRoots`: a page of the normalized absolute roots, in configured order. An empty array means file inputs are disabled (no `--input-root` was configured); pass content inline instead.
- `inputRootCount`: the total number of configured roots.
- `inputRootsComplete`: `true` only when this single response holds every root (first page, nothing omitted, no next page).
- `inputRootsNextCursor`: the offset to pass as `inputRootCursor` for the next page, or `null` on the last page. The page size follows the result byte budget. To get the full inventory, repeat the call with `inputRootCursor` set to the previous `inputRootsNextCursor` until it is `null`. A cursor outside `0..inputRootCount` is rejected.
- `inputRootsOmitted`: roots skipped because a single root path is too long to fit any page; they stay enforced but cannot be listed.

Discovery workflow: call `sqlharness_capabilities`, pick a root from `inputRoots`, place the file under that root, and pass its absolute path as `file`. Roots are fixed at startup. SQLHarness never reads client configuration, never creates directories, and MCP `roots/list` never widens the allowlist.

Input-path rejections name the reason and echo the caller path (credential-filtered, control characters replaced, URL or UNC `user:password@` userinfo masked, truncated at 120 characters), for example `The input path was rejected: not an absolute path. Path: ...`. A well-formed path outside every configured input root reports `The input path is outside every configured input root` and lists the configured roots when they fit a small bound (at most 8 roots and 150 characters of root text); when only a prefix fits it says `First K of N configured roots: ...` and points to the `sqlharness_capabilities` `inputRoots` pages (`inputRootCursor`) for the rest, and when not even one root fits it states the count and points to those pages. Rejection messages are clipped to the effective `maxCellChars` limit (default 2048). With no roots configured the message says file inputs are disabled.

- SQL tools take exactly one of inline `sql` or `file`; the plan tool takes exactly one of inline `content` or `file`. Inline payloads above 1 MiB are rejected and must arrive as files under an input root. SQL and plan files are admitted up to 16 MiB, matching the CLI/Core SQL bound.
- Parameter-set files must use the `.sqljson` extension and are limited to 16 MiB per file and 32 MiB total raw file bytes per logical call. The call-wide budget covers every selected parameter-set file, including repeated paths; SQLHarness charges each occurrence once in input order. Files are parsed with the shared Core parser: only `name` and `parameters`; a UTF-8 BOM, JSON comments, and trailing commas are accepted. Values are never copied into reports.
- Fixed parameter files passed with CLI `--params-json` use a `.sqljson` object containing only a `parameters` array and use the same 16 MiB per-file and 32 MiB total raw-ingress limits as parameter-set files. Each entry supports scalar `{name, type, value}` plus SQL Server `kind: "tvp"` with exactly one of `rows` or `rowsFrom`, or `kind: "list"` with exactly one of `values` or `valuesFrom`. Inline cells are culture-invariant strings or JSON null. These non-scalar kinds are limited by `sqlharness_capabilities`; mutation does not support them, and source SQL must be read-only.
- Raw file ingress is separate from decoded/materialized parameter memory: CLI capabilities reports `limits.parameterFileIngress.maxFileBytes` and `limits.parameterFileIngress.maxTotalBytes`; MCP capabilities reports the same fields under `parameterFileIngress`. `materializedParameterBytes` remains 32 MiB in both documents. Allowing a larger JSON file does not permit more materialized SQL parameter data. MCP accepts `parameterSetFiles`; it has no fixed-parameter-file argument.
- Parameters travel as `{name, type, value}` triples with culture-invariant string values or JSON null. The adapter maps them onto the existing Core binder and keeps its name, duplicate, and type validation; there is no second SQL validator and no second type list. MCP SQL parameters use the same scalar fields and may also carry `kind`, `rows`, `rowsFrom`, `values`, or `valuesFrom` for table-valued and list inputs.
- A matrix argument carries a typed name and type plus a `values` array of at least two distinct elements, each a culture-invariant string or JSON null. Every element reaches the Core binder whole: a comma is an ordinary character, an empty string is a text value, and JSON null is a typed NULL of the matrix type (`null`, `""`, and the text `"null"` are three distinct values). Parameters and matrix values are never joined into or split from text on this path; the CLI `--matrix name:type=v1,v2` text grammar is unchanged. The same null carries through to the result: a compare-matrix cell's `parameterValue` is JSON null only for the cell that bound a typed NULL matrix value, and a plain string for every other cell.

## Client workspace roots

On the handshake revisions (`2025-06-18`, `2025-11-25`), when the client
declares the `roots` capability, SQLHarness requests `roots/list` after
`initialized`, and again on `roots/list_changed` only if the client declared
`listChanged`. At most 32 roots are kept. On `2026-07-28` a server-to-client
`roots/list` succeeds only while the originating `tools/call` is still waiting
for it, and a background fetch hangs until the 5 s timeout. Because a roots
fetch must never block a tool call, SQLHarness does not request roots on
`2026-07-28` sessions: their roots stay unknown (NULL). MRTR and
`subscriptions/listen` are not implemented. A successful answer is recorded in
the local activity journal for the dashboard and nothing else: client roots
never widen `--input-root`, never authorize a file input, and never change a
tool result. A failed or slow (5 s) request keeps the previous snapshot; stderr
names only the failure class.
