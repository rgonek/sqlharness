### Spec Compliance

- ❌ Issues found: the evaluator suppresses an unexplained fingerprint mismatch when the only recorded difference is informational; missing JSON properties/condition collections are not consistently distinguished from empty or equal data; and paired-case output paths include the opaque pair key.
- ✅ The diff contains all five requested study artifacts. The fixture has 17 cases, and the verifier uses a hard-coded expected table for all 17, compares the supplied basis, checks availability/gaps/difference identities, hidden-output cases, and per-item UTF-8 byte counts.
- ⚠️ Cannot verify from diff: the future route map's claims about current dashboard selector and operation-pair behavior. No source hunks are included and I did not crawl outside the diff. Production serializer, journal prevalence, and browser/transport behavior are also outside the study's evidence.

### Strengths

- The change stays in the offline evidence directory and keeps the supplied `basis` in each result; the verifier compares it with the fixture.
- Expected availability and difference signatures are hard-coded independently of evaluator helpers. The verifier covers all named fixtures and checks exact reported UTF-8 byte counts against its own serialization.
- Hidden-value, ambiguous-pairing, informational-relation, and byte/item-cap scenarios are represented. The design clearly separates explanation output from grouping and provides UI wording plus both future comparison paths.

### Issues

#### Critical (Must Fix)

None.

#### Important (Should Fix)

- `plans/118-evidence/evaluate.ps1:117-127` — A differing fingerprint produces `conditions-unavailable` only when `$differences.Count -eq 0`. The named “changed comparison mode only” case has different fingerprints and an informational-only difference, so the evaluator returns `complete` with no gap. An informational change does not explain a measurement-fingerprint mismatch; the candidate contract requires a gap when no measurement-condition change explains it. Check for a provable measurement-condition difference (or explicitly model which fingerprint inputs it explains), and make this case partial with `conditions-unavailable`.
- `plans/118-evidence/evaluate.ps1:25, 43-51, 101` — Missing JSON properties and missing condition collections can be silently treated as no evidence or as empty collections. `Compare-Scalar` returns when either side is null, and `Compare-Parameters` receives the collection without checking whether the property exists; an absent collection can therefore produce added/removed parameter claims, while absent values can produce no gap and leave the explanation complete. The “typed NULL versus absent value” fixture uses an explicit `state: "missing-property"` wrapper, so it does not test a genuinely absent JSON property. Distinguish property presence and unknown/omitted collections before comparison; emit a gap and do not infer equality or additions/removals from absent data. Also preserve a retained typed NULL as an explicit present before/after value rather than dropping it: `Add-Difference` at lines 15-16 omits any null-valued side.
- `plans/118-evidence/evaluate.ps1:42-43, 113` — Paired-case parameter fields are built with the pair key in the path (`cases.$key.parameters...`). That key is an internal pairing aid and can be an opaque journal-keyed value; emitting it in the public `field` string leaks it even though `caseIdentity` is separately present. Use a key-free field path and add a paired-case parameter-difference fixture whose verifier asserts the pair key is absent from all explanation fields.

#### Minor (Nice to Have)

- `plans/118-evidence/verify.ps1:39-51` — The oracle compares field/kind/relation/identities but not expected before/after display values. For overflow it checks 32 unique identities, that the first is `@p00`, and the omitted count, but not the exact retained identity set from the 48 fixture inputs. Assert expected retained values and the exact sorted overflow identity set so an incorrect value projection or invented/substituted item cannot satisfy the oracle.

### Assessment

**Task quality:** Needs fixes

**Reasoning:** The study is well scoped and its verifier is independent at the category/signature level, but named input states can still be collapsed or misreported, an informational difference masks an unexplained fingerprint mismatch, and an internal case key can escape in output. These undermine core contract claims until corrected.
