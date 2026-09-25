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

Canonical baselines are accepted only when they are states the supported alpha.14 normalizer can produce. The reader rejects duplicate global file-source formats or exit-code values, empty global-config wrappers, invalid command paths or hierarchy, invalid option names and aliases, requiredness-inconsistent non-variadic arity, invalid variadic required/min/max combinations, invalid positional ordering or variadic placement, impossible source combinations, reversed scalar choices or contradictory choice domains, unsupported status/deprecation values, and global/local accepted-name collisions before comparison. These malformed-but-valid JSON baselines are baseline diagnostics with exit `3`, never `UNEXPECTED_ERROR` (`4`).

`OPENCLI_DEFAULT` and `OPENCLI_CHOICE` are bounded exit-`3` validation diagnostics. They mean a default or constrained choice is not representable by its declared type; for example, a fractional value cannot be an `integer`. Such values are rejected before snapshotting, so `validate`, `snapshot`, `check`, and `diff` cannot accept a structurally non-reflexive description.
