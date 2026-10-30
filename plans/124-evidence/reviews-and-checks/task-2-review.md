### Spec Compliance

- Issues found: the original and complete candidate document receive only ProfileStore's structural checks; runtime authentication and template/regex validation apply solely to the edited profile. Consequently an already unusable sibling does not make the document read-only as required (ProfileWriter.cs:40,44,64; ProfileStore.cs:51-84).
- Cannot verify from this task: Task 3 form behavior and Task 4 full Windows gate. The reported focused run passed 183 tests; it is not the full gate. The red evidence covers one behavioral field-error change; the initial availability assertion and compilation failure do not demonstrate behavioral TDD for the remaining cases.

### Strengths

- ProfileWriter.cs:42-70 checks the byte-derived revision, replaces only the chosen value, validates the assembled document structurally, rechecks the revision, and returns the in-lock snapshot. ProfileWriterTests.cs:10-27 exercises sibling byte preservation and stale rejection.
- DashboardProfiles.cs:67-95 bounds streamed input, rejects duplicate and unknown members, maps typed results to fixed responses, and DashboardSecurity.cs:104-108 admits PUT through existing write guards. DashboardProfilesTests.cs:19-86 covers guards and locked-file behavior.
- ProfileWriterTests.cs:82-96,120-157 exercises a Windows destination lock, injected post-creation failure, cancellation and cleanup without touching live targets. The noncooperating-writer race and cancellation boundary are accurately reported.

### Issues

#### Critical (Must Fix)

None.

#### Important (Should Fix)

1. ProfileWriter.cs:40,44,64; ProfileStore.cs:51-84 - Full document runtime validation is not performed. For example, an existing sibling with auth: "bogus" or an invalid regex passes ProfileStore.Parse, while ValidateProfile is called only for the submitted profile; editing another profile then writes the supposedly invalid document. This violates the read-only-invalid-document and complete-candidate validation contract. Apply shared runtime configuration validation to every profile in both original and candidate, without resolving environment values, and test invalid sibling auth, TLS, regex and template with byte preservation.

2. ProfileWriter.cs:66-70 - Replacing the destination with a newly created temporary file does not preserve the original file's security metadata. On Unix a pre-existing 0600 targets.json can be replaced by a file created with default permissions under the process umask, potentially making profile configuration accessible to other users; on Windows the temp inherits its directory ACL instead of necessarily retaining an explicitly restricted file ACL. Preserve the destination's applicable permissions/security descriptor during atomic replacement and test a restricted destination on supported platforms.

#### Minor (Nice to Have)

None.

### Assessment

**Task quality:** Needs fixes.

**Reasoning:** The concurrency and guarded API design largely follow the task, but complete-document validation and file security metadata preservation need correction before the persistence layer is trustworthy. Reported focused results were inspected; no test suite was rerun. Outside-diff checks were limited to AuthSpec.Parse, TargetResolver.ResolveProfile, TargetProfile, ProfileStore serializer options (validation parity) and the settings writer's replacement operations (security metadata risk).