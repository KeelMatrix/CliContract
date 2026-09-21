# CliContract Compatibility Rules

This document defines the v1 change-classification contract for KeelMatrix CliContract. It applies to OpenCLI `1.0.0-alpha.14` JSON and YAML input and canonical manifest schema version `1`.

## Categories and failure policy

- **breaking** changes can make an existing invocation invalid and fail by default.
- **warning** changes are represented changes that may alter automation behavior and fail only with `--fail-on warning`.
- **info** changes are reported for review but do not fail either threshold.

`--fail-on breaking` is the default. `--fail-on warning` gates both breaking and warning findings. Suppression is explicit with `--ignore`; a suppression file can contain a string array of diagnostic codes or an object with `codes` and `paths` arrays. Suppressions affect findings only, never parsing, schema-version, baseline, or invocation errors.

## Stable diagnostic catalog

| Code | Category | Change |
| --- | --- | --- |
| `KMCLI001` | info | Command added |
| `KMCLI002` | info | Optional option added |
| `KMCLI003` | info | Optional argument added |
| `KMCLI004` | info | Alias added |
| `KMCLI005` | info | Summary or description/help changed |
| `KMCLI101` | breaking | Option removed |
| `KMCLI102` | breaking | Argument removed |
| `KMCLI103` | breaking | Command removed |
| `KMCLI104` | breaking/info | Alias removed or added; removal is breaking, addition is informational |
| `KMCLI105` | breaking | Optional parameter became required |
| `KMCLI106` | breaking | Accepted arity narrowed |
| `KMCLI107` | breaking | Explicit type or allowed-value domain narrowed |
| `KMCLI108` | breaking | Required option or argument added |
| `KMCLI201` | warning | Default value changed |
| `KMCLI202` | warning | Status or deprecation state changed when represented |
| `KMCLI203` | warning | Other represented type/domain change that is not a narrowing |

Each finding has a stable code, category, logical command path, and bounded message. Paths use forms such as `root / deploy / --region`; source filesystem paths are not diagnostic paths.

## Stable tool diagnostics

| Code | Exit code | Meaning |
| --- | ---: | --- |
| `UNEXPECTED_ERROR` | `4` | Unexpected tool failure |

## Semantics

The analyzer compares commands by logical path, options by normalized long name, and arguments by name. Source declaration order does not affect command, option, alias, or choice collections. Argument declaration order remains in the canonical manifest because positional order is part of the contract; reordering positional arguments is represented by the manifest and should be reviewed as a contract change.

Breaking rules are command/option/argument removal, callable alias removal, optional-to-required changes, arity narrowing, required type narrowing, and allowed-value domain narrowing. Adding an optional command/option/argument or alias is informational. Adding a required parameter is breaking. Default changes are warnings. Summary/description changes are informational by default. Deprecation/status changes are warnings only when represented by the supported format.

Type/domain widening does not produce a breaking finding. A changed represented type/domain that cannot be proven to be a narrowing is reported as `KMCLI203` warning.

## Supported upstream boundary

The adapter requires `opencliVersion: 1.0.0-alpha.14`, required `info.title`, `info.binary`, and `info.version`, and validates all recognized in-contract fields plus the pinned schema's required structure. Informational `info` and install metadata are preserved in `CanonicalManifest.Info` but are not compatibility semantics. Other schema-declared fields outside the compatibility contract remain accepted, validated, and ignored, including examples, hidden/kind fields, exit-code metadata, and choice descriptions. `x-*` extensions are accepted and ignored. Unknown non-extension fields on the root, command, argument, flag, choice, and other recognized schema objects fail closed. Invalid recognized values fail closed. Schema-valid argument `passthrough` is accepted and ignored because it is not represented in the compatibility contract.

The adapter supports nested commands, root/global flags, aliases, positional arguments, requiredness, represented arity, scalar choices, scalar defaults, `$ENV`/`$FILE` alternative sources, summaries, descriptions, and bounded Unicode text. It does not claim support for deprecation/status because alpha.14 does not represent that contract field.

## Failure and privacy behavior

Unknown upstream or canonical manifest versions are rejected explicitly. JSON duplicate keys, malformed JSON/YAML, YAML anchors/aliases, excessive size/depth/node/string/collection limits, invalid recognized fields, and incompatible baselines are errors, never compatible results. The described CLI is never started.

The tool never fetches a network resource and makes no network access while parsing or comparing. Remote schema references that would require resolution, including `$ref`, `$dynamicRef`, `$recursiveRef`, includes, and remote document references, fail closed with `OPENCLI_REMOTE_REFERENCE` (CLI exit code `3`). In contrast, schema-valid scalar URL values in informational OpenCLI metadata, including `info.contact.url`, `info.license.url`, and `info.install.url`, are accepted and preserved verbatim in `CanonicalManifest.Info`. These values are data only and are never fetched. The optional post-comparison telemetry request is separate from schema parsing and comparison and is disabled by the documented opt-out and development/CI controls.

Diagnostics do not echo complete documents, defaults, descriptions, or schema fragments. After a successful nonempty comparison, the tool requests one failure-isolated activation through the published `KeelMatrix.Telemetry` package without passing schema-derived values. `--no-telemetry`, `KEELMATRIX_NO_TELEMETRY=1`, `KEELMATRIX_DEVELOPMENT=true`, and `CI=true` disable the request; no schema data is emitted.

## Schema validity versus compatibility

`validate` answers whether one document is parseable and supported. `snapshot` creates a versioned canonical representation. `check` and `diff` compare two valid descriptions. A valid schema can still be incompatible with a valid baseline; compatibility is a relationship between two manifests, not a property of one input.
