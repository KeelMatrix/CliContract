# First-release acceptance map

Generated from the current read-only acceptance checklist by `scripts/generate-acceptance-map.ps1`; production named-run evidence is independently resolved, while the self-test uses only internal fixture metadata; row numbers and criterion text are not hand-maintained.

| # | Criterion | Status | Candidate-SHA evidence or disposition |
|---:|---|:---:|---|
| 1 | Exact command evidence is captured | MET | candidate_sha=1111111111111111111111111111111111111111; proof_kind=reproducible; criterion_sha256=ab9b8ca51fadfcead928f1aacc40570381cd3707ad4a6d28dac4762af4142e1d; proof=command=pwsh -NoProfile -File ./scripts/verify-error-taxonomy.ps1; output=ERROR_TAXONOMY=PASS |
| 2 | Reviewer judgement names its artifacts | MET | candidate_sha=1111111111111111111111111111111111111111; proof_kind=judgement; criterion_sha256=663b98df70682ac27f2ecd758814ef7487e74f2389bb3ef5d7d0b7b9994c0d27; proof=judgement=artifacts=MANIFEST.md,COMPATIBILITY-RULES.md; rationale=reviewer compared the manifest and compatibility contract for this criterion |
| 3 | Multi-package evidence is not applicable | N/A | criterion_sha256=adb63f9b4c5683fb26353e87df8c78651a67b976e6f0c72b3add564060e88b09; N/A only applies to repositories shipping multiple packages |
