# CLI error taxonomy

This table is the public error and exit-code contract. The built help text and the package consumer smoke exercise the same role distinctions.

| Failure role | Stable code family | Exit | Meaning |
| --- | --- | ---: | --- |
| Missing source path | `INPUT_NOT_FOUND` | 2 | The requested source path does not exist. |
| Missing canonical baseline path | `BASELINE_NOT_FOUND` | 2 | The requested baseline path does not exist. |
| Present source read/encoding/size/parse/validation failure | `INPUT_UNREADABLE`, `INVALID_UTF8`, `INPUT_TOO_LARGE`, or source diagnostic | 3 | The source exists but cannot be accepted as the supported contract. |
| Present canonical baseline read/encoding/size/parse/validation failure | `BASELINE_UNREADABLE`, `INVALID_UTF8`, `INPUT_TOO_LARGE`, or baseline diagnostic | 3 | The baseline exists but is not an accepted canonical manifest. |
| Suppression/configuration failure | `IGNORE_*`, `INVALID_IGNORE`, or invocation diagnostic | 2 | An explicit invocation or suppression input is invalid. |
| Output destination failure | `OUTPUT_NOT_WRITABLE` | 2 | The requested snapshot destination cannot be written. |
| Unexpected internal failure | `UNEXPECTED_ERROR` | 4 | An error outside the bounded input, baseline, invocation, or output contract occurred. |

The adapter is offline. Unsupported versions, malformed documents, duplicate keys, invalid recognized fields, ambiguous accepted invocations, and unsupported canonical manifests are present-input or baseline failures and therefore return exit `3`. Missing paths are the only source/baseline path failures that return exit `2`.

## Verification contract

An error-taxonomy claim in a repository acceptance record must include the exact command and captured output, a rerunnable repository checker, a candidate-bound CI run, or an explicit judgement naming the taxonomy artifact and rationale. A path such as `docs/ERROR-TAXONOMY.md` alone is not sufficient; acceptance-map linting rejects path-only proof and independently checks named run metadata. The production path resolves and invokes the application returned by `Get-Command gh -CommandType Application`, not a caller-defined PowerShell function. The self-test uses its non-exported fixture seam and requires no `gh`, network, or `GH_TOKEN`. Mechanical checks do not establish the semantic relevance of a judgement rationale; that remains reviewer attestation.

Canonical baselines are accepted only when they are states the supported alpha.14 normalizer can produce. Nonempty whitespace-only source-shaped strings are preserved as data; empty required strings, empty aliases, and duplicate aliases remain invalid. Option names are checked with the same `--` + `TrimStart('-')` construction rule used by normalization, so whitespace-containing and dash-only names are not false positives. The reader also rejects missing required metadata, empty license/example/install fields, contact/install presence violations, non-letter command segments or invalid hierarchy, duplicate global file-source formats or exit-code values, empty global-config wrappers, option names outside the shared construction rule, invalid option types and aliases, requiredness-inconsistent non-variadic arity, invalid variadic required/min/max combinations, invalid positional ordering or variadic placement, impossible source combinations, reversed scalar choices or contradictory choice domains, unsupported status/deprecation values, and global/local accepted-name collisions before comparison. These malformed-but-valid JSON baselines are baseline diagnostics with exit `3`, never `UNEXPECTED_ERROR` (`4`).

`OPENCLI_DEFAULT` and `OPENCLI_CHOICE` are bounded exit-`3` validation diagnostics. They mean a default or constrained choice is not representable by its declared type; for example, a fractional value cannot be an `integer`. Such values are rejected before snapshotting, so `validate`, `snapshot`, `check`, and `diff` cannot accept a structurally non-reflexive description.
