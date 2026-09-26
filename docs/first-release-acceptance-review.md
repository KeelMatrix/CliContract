# First-release acceptance self-review

This record covers candidate `bca89f0ce48420ab41b0a3bfc966d2546080563b` against the current read-only KeelMatrix first-release acceptance checklist. The record itself is committed by the following repository commit so the candidate and its durable review record remain separately identifiable.

## Durable inputs and output

- Evidence input: [`fixtures/acceptance-map/first-release-evidence.json`](../fixtures/acceptance-map/first-release-evidence.json)
- Generated 170-row map: [`fixtures/acceptance-map/first-release-map.md`](../fixtures/acceptance-map/first-release-map.md)
- Generator: [`scripts/generate-acceptance-map.ps1`](../scripts/generate-acceptance-map.ps1)
- Linter: [`scripts/lint-acceptance-map.ps1`](../scripts/lint-acceptance-map.ps1)

The evidence input covers every checklist item by one-based `criterion_number`. The generated map records the exact criterion text and SHA-256 hash, so checklist drift is visible during linting.

## Candidate lint result

```text
ACCEPTANCE_MAP_LINT=PASS checklist_rows=170 map_rows=170 met_rows=151 unmet_rows=0 na_rows=19 missing=0 duplicate_numbers=0 criterion_text_mismatches=0 criterion_hash_mismatches=0 missing_candidate_evidence=0 met_without_candidate_sha=0 met_without_anchor=0 invalid_run_evidence=0 checker_evidence_failures=0 evidence_kind_mismatches=0 na_without_justification=0 unmet_without_justification=0 candidate_sha=bca89f0ce48420ab41b0a3bfc966d2546080563b
```

C81 has its own telemetry/privacy evidence in row 81. C101 has its own `gh repo view KeelMatrix/CliContract --json description --jq .description` command evidence in row 101.

## Trust model

The acceptance tooling mechanically enforces candidate-bound GitHub Actions metadata, exact criterion text and hashes, exact command/output anchors, rerunnable checker output, repository-path containment, rejection of generic or path-only proof, and visible `proof_kind` distinction. Production run metadata is resolved from the application returned by `Get-Command gh -CommandType Application`; the private module exports only production wrappers. Judgement rows require a criterion-specific artifact declaration and are marked `proof_kind=judgement`, but their semantic relevance is reviewer attestation and is not mechanically proven.

This is an implementer self-review record. It does not authorize tagging, publication, or release.
