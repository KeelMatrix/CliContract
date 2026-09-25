# CliContract Compatibility Rules

This document defines the v1 change-classification contract for KeelMatrix CliContract. It applies to OpenCLI `1.0.0-alpha.14` JSON and YAML input and canonical manifest schema version `2`.

## Categories and failure policy

- **breaking** changes can make an existing invocation invalid and fail by default.
- **warning** changes are represented changes that may alter automation behavior and fail only with `--fail-on warning`.
- **info** changes are reported for review but do not fail either threshold.

`--fail-on breaking` is the default. `--fail-on warning` gates both breaking and warning findings. Suppression is explicit with `--ignore`; a suppression file can contain a string array of diagnostic codes or an object with `codes` and `paths` arrays. Suppressions affect findings only, never parsing, schema-version, baseline, or invocation errors.

## Stable diagnostic catalog

| Code | Category | Change |
| --- | --- | --- |
| `KMCLI001` | info | Callable command added |
| `KMCLI002` | info | Optional option added |
| `KMCLI003` | info | Optional argument added |
| `KMCLI004` | info | Alias added |
| `KMCLI005` | info | Represented informational metadata, install guidance, summary/description text, or choice description changed |
| `KMCLI006` | info | Represented hidden/help example metadata changed |
| `KMCLI101` | breaking | Option removed |
| `KMCLI102` | breaking | Argument removed |
| `KMCLI103` | breaking | Callable command removed |
| `KMCLI104` | breaking/info | Alias removed or added; removal is breaking, addition is informational |
| `KMCLI105` | breaking | Optional parameter became required |
| `KMCLI106` | breaking | Accepted arity narrowed |
| `KMCLI107` | breaking | Explicit type or allowed-value domain narrowed |
| `KMCLI108` | breaking | Required option or argument added |
| `KMCLI109` | breaking | Positional argument slot changed, including insertion before or between existing arguments |
| `KMCLI110` | breaking | CLI binary invocation name changed |
| `KMCLI111` | breaking/info | Callable action became a non-runnable group (breaking) or runnable kind changed (info) |
| `KMCLI112` | breaking/info | Argument passthrough changed; disabling accepted post-`--` forms is breaking, enabling it is informational |
| `KMCLI201` | warning | Default value changed |
| `KMCLI202` | warning | Status or deprecation state changed when represented |
| `KMCLI203` | warning | Other represented type/domain change that is not a narrowing |
| `KMCLI204` | warning | Represented default-source or global file-source configuration changed |
| `KMCLI205` | warning | Global or command exit-code contract changed |

Each finding has a stable code, category, logical command path, and bounded message. Paths use forms such as `root / deploy / --region`; source filesystem paths are not diagnostic paths.

## Selected tool diagnostics

This is a selected compatibility-diagnostic catalog. The complete role-aware exit taxonomy is [`docs/ERROR-TAXONOMY.md`](docs/ERROR-TAXONOMY.md); source and baseline path roles are intentionally documented there rather than repeated as findings.

| Code | Exit code | Meaning |
| --- | ---: | --- |
| `OPENCLI_REMOTE_REFERENCE` | `3` | Remote schema reference or include is unsupported; no network resolution is attempted |
| `OPENCLI_DUPLICATE_PARAMETER` | `3` | An argument or option collection contains duplicate normalized names |
| `OPENCLI_DUPLICATE_COMMAND_PATH` | `3` | Different OpenCLI command keys normalize to the same canonical command path |
| `OPENCLI_DUPLICATE_INVOCATION` | `3` | Different commands or aliases accept the same logical invocation |
| `OPENCLI_GROUP_COMMAND` | `3` | A group command declares local positional arguments or flags |
| `OPENCLI_ARGUMENT_ORDER` | `3` | A required positional argument follows an optional positional argument |
| `OPENCLI_VARIADIC` | `3` | A variadic argument/flag violates its placement or requiredness rules |
| `OPENCLI_ARITY` | `3` | Item bounds are invalid for the declared variadic parameter |
| `OPENCLI_DEFAULT` | `3` | A typed default cannot be represented by its declared flag type |
| `OPENCLI_CHOICE` | `3` | A constrained choice cannot be represented by its declared parameter type |
| `OPENCLI_DEFAULT_SOURCE` | `3` | A `$FILE` alternative source has no applicable global config file source |
| `OPENCLI_NUMBER` | `3` | An explicitly numeric scalar is not a supported finite canonical JSON number |
| `UNEXPECTED_ERROR` | `4` | Unexpected tool failure |

## OpenCLI alpha.14 semantic coverage matrix

The adapter accepts the pinned OpenCLI `1.0.0-alpha.14` field set below. Every recognized field belongs to exactly one coverage class:

| Class | Meaning |
| --- | --- |
| (a) | Invocation or compatibility semantic. It is preserved canonically and participates in the compatibility decision. |
| (b) | Represented warning or informational semantic. It is preserved canonically and changes are classified as warnings or information where applicable. |
| (c) | Explicitly outside the compatibility promise. It is accepted as an extension, validated only for bounded JSON shape, and does not affect the compatibility decision. |

| OpenCLI field or structure | Class | Canonical treatment |
| --- | --- | --- |
| `opencliVersion`, command keys, `commands` | (a) | Pinned source identity and accepted command-name graph roots |
| `info.binary` | (a) | Root invocation identity |
| command `aliases` | (a) | Accepted invocation names at each command segment |
| `args`/`flags` collections and parameter `name` | (a) | Positional slots and option identities |
| parameter `type`, `variadic`, `minItems`, `maxItems`, `required` | (a) | Accepted lexical domain and arity |
| parameter `choices[].value` | (a) | Constrained accepted domain |
| argument `passthrough` | (a) | Accepted post-`--` forms |
| command `kind` | (a) | Runnable action/group state |
| `global.flags` | (a) | Inherited global options and accepted invocations; stored in `GlobalOptions` |
| `global.config` (`json`, `toml`, `yaml`) | (b) | Preserved and compared as a configuration warning |
| `info.title`, `info.summary`, `info.description`, `info.version` | (b) | Preserved and compared as `KMCLI005` informational metadata |
| `info.install` and install fields `name`, `command`, `url`, `description` | (b) | Preserved and compared as `KMCLI005` informational installation guidance |
| command `summary`, `description` | (b) | Preserved and compared as `KMCLI005` informational help text |
| command `hidden`, `examples` (`title`, `content`) | (b) | Preserved and compared as `KMCLI006` informational visibility/example metadata |
| parameter `summary`, `description` | (b) | Preserved and compared as `KMCLI005` informational help text |
| parameter `hint`, `hidden` | (b) | Preserved and compared as `KMCLI006` informational help metadata |
| parameter `default` and `alternativeSources` (`type`, `property`) | (b) | Preserved and compared as warnings because automation defaults can change |
| `global.exitCodes` and command `exitCodes` (`code`, `status`, `summary`, `description`) | (b) | Preserved and compared as `KMCLI205` warnings |
| `info.license` fields `name`, `spdxId`, `url`; `info.contact` fields `name`, `email`, `url` | (b) | Preserved and compared as `KMCLI005` informational metadata; never fetched |
| `choices[].description` | (b) | Preserved and compared as `KMCLI005` informational choice help text |
| Any `x-*` extension and its contents | (c) | Accepted and ignored for compatibility; an identical document with only an `x-*` change has no compatibility finding |

This matrix is implemented by the canonical contract and consumed by normalization and comparison. In particular, command comparison uses the accepted invocation-name graph, all type transitions use the accepted lexical-domain relation, represented exit-code changes are warnings, and every represented class-(b) metadata/help field produces a stable finding when it changes. Canonical schema version `2` keeps `GlobalOptions` separate from root-local `Root.Options`; comparison evaluates the effective inherited-plus-local option surface at every command.

## Semantics

The analyzer compares the accepted invocation-name graph (a materialized trie of primary names and aliases at every command segment), options by their accepted long-name graph, and arguments by name and positional slot. A rename without the old alias is breaking; retaining the old name preserves the old invocation, including descendants below a renamed group. Alias removal is breaking only when an accepted invocation is actually removed. Source declaration order does not affect command, option, alias, or choice collections. Argument declaration order remains in the canonical manifest because positional order is part of the contract; reordering or inserting an argument before or between existing slots produces breaking finding `KMCLI109`, while a trailing optional argument is informational. Different command keys that normalize to the same logical path are rejected with `OPENCLI_DUPLICATE_COMMAND_PATH` rather than being merged or dropped. Missing command ancestors are derived `group` nodes; explicit group-to-derived-group equivalence is compatible, while an action-to-derived-group transition is breaking. Callable command diagnostics are based on runnable actions, not declaration-record presence.

Global options apply to every command. Root-local options apply only to the root command. Moving an option between those scopes is compared as a change to the effective surface of every affected command, in both directions, including requiredness, arity, type and choices, defaults, alternative sources, aliases, and represented help/hidden metadata. A global accepted name and a command-local accepted name colliding on the same command is invalid canonical state and returns exit `3`.

Breaking rules are command/option/argument removal, callable alias removal, binary invocation rename, action-to-group transitions, optional-to-required changes, arity narrowing, required type narrowing, allowed-value removal from an explicitly constrained current domain, allowed-value domain narrowing, and loss of accepted post-`--` argument forms when `passthrough` changes from true to false. Adding an optional command/option/argument or alias is informational. Adding a required parameter is breaking. Enabling argument passthrough is informational. Default changes, alternative-source type/property/order changes, and global file-source configuration changes are warnings. Changes to represented `info` metadata, install guidance, command/parameter summary or description/help, examples/visibility metadata, and choice descriptions are informational by default and use `KMCLI005` or `KMCLI006` as listed in the catalog. Deprecation/status changes are warnings only when represented by the supported format.

Type/domain widening does not produce a breaking finding. The accepted-domain relation covers every supported OpenCLI type pair (`string`, `number`, `integer`, and `boolean`) and then applies constrained choice sets; any removal of previously accepted lexical values is breaking. Choice and default values are validated against the declared type before canonicalization, so a fractional integer value or other mismatch is rejected with exit `3` rather than becoming a non-reflexive manifest. Canonicalization, typed validation, and constrained-domain comparison share the same exact finite-number representation, including arbitrary magnitudes outside `decimal` range. A changed represented type/domain that cannot be proven to be a narrowing is reported as `KMCLI203` warning.

## Supported upstream boundary

The adapter requires `opencliVersion: 1.0.0-alpha.14`, required `info.title`, `info.binary`, and `info.version`, and validates all recognized in-contract fields plus the pinned schema's required structure. The binary is invocation identity: every command key must begin with `info.binary`, and a binary rename is breaking. Command `kind` is preserved as runnable state; omitted kind is normalized as an action for explicit commands, while omitted ancestors and root are materialized as derived groups. Informational `info` and install metadata are preserved in `CanonicalManifest.Info` and compared as `KMCLI005` info findings; binary is compatibility semantics. Global file-source configuration and global/command exit codes are preserved canonically. Argument `passthrough` is normalized with a default of false and is compatibility semantics because it controls how accepted forms after `--` are interpreted. Help metadata, examples, hidden state, defaults, alternative sources, exit-code metadata, and choice descriptions remain represented so their changes produce the documented stable findings instead of being discarded. `x-*` extensions are accepted and ignored. Unknown non-extension fields on the root, command, argument, flag, choice, and other recognized schema objects fail closed. Invalid recognized values fail closed.

The pinned conformance corpus is [`fixtures/opencli/alpha14-conformance-corpus.json`](fixtures/opencli/alpha14-conformance-corpus.json), with its tagged-test inventory in [`docs/OPENCLI-ALPHA14-CONFORMANCE.md`](docs/OPENCLI-ALPHA14-CONFORMANCE.md). It covers official valid structure, recognized fields, opaque extensions, arguments, flags, variadic bounds, aliases, choices, defaults, alternative sources, command kind, binary identity, exit codes, config, unknown versions, and invalid combinations. Unit tests execute its validity oracle and separately compare equivalent JSON and YAML documents. The repository does not invoke a remote validator at runtime; the local `ocli` executable was not available during this refresh, so differential validation is intentionally not a release prerequisite.

The adapter supports nested commands, root/global flags, aliases, positional arguments, requiredness, represented arity, scalar choices, scalar defaults, `$ENV`/`$FILE` alternative sources, summaries, descriptions, and bounded Unicode text. It does not claim support for deprecation/status because alpha.14 does not represent that contract field.

Finite JSON numbers and recognized YAML integer/float spellings are compared by exact numeric value. Recognized YAML floats include trailing-dot exponent mantissas such as `5.e2` and signed/exponent-sign variants. YAML `.inf` and `.nan` spellings are outside the JSON-number boundary: explicit `!!float` forms fail with `OPENCLI_NUMBER` and exit code `3`, while untagged forms remain strings.

## Failure and privacy behavior

Unknown upstream or canonical manifest versions are rejected explicitly. The canonical reader also rejects every state outside the source-producible alpha.14 contract before compatibility comparison: duplicate source formats or exit codes, empty global-config wrappers, invalid paths and hierarchy, invalid option/alias forms, requiredness-inconsistent non-variadic arity, invalid variadic required/min/max combinations, invalid argument ordering or variadic placement, impossible default/source combinations, reversed scalar choices, contradictory choice domains, unsupported status/deprecation values, and global/local option collisions. JSON duplicate keys, duplicate normalized parameter names, malformed JSON/YAML, YAML anchors/aliases, excessive size/depth/node/string/collection limits, invalid recognized fields, and incompatible baselines are errors, never compatible results. The described CLI is never started.

The tool never fetches a network resource and makes no network access while parsing or comparing. Remote schema references that would require resolution, including `$ref`, `$dynamicRef`, `$recursiveRef`, includes, and remote document references, fail closed with `OPENCLI_REMOTE_REFERENCE` (CLI exit code `3`). In contrast, reference-looking values inside opaque `x-*` extension subtrees are accepted as metadata and never interpreted. Schema-valid scalar URL values in informational OpenCLI metadata, including `info.contact.url`, `info.license.url`, and `info.install.url`, are accepted and preserved verbatim in `CanonicalManifest.Info`. These values are data only and are never fetched. The optional post-comparison telemetry request is separate from schema parsing and comparison and is disabled by the documented opt-out and development/CI controls.

Diagnostics do not echo complete documents, defaults, descriptions, or schema fragments. After a successful nonempty comparison, the tool requests one failure-isolated activation through the published `KeelMatrix.Telemetry` package without passing schema-derived values. `--no-telemetry`, `KEELMATRIX_NO_TELEMETRY=1`, `KEELMATRIX_DEVELOPMENT=true`, and `CI=true` disable the request; no schema data is emitted.

Missing source or baseline paths return exit `2`; present but unreadable, invalid-UTF-8, oversized, malformed, unsupported, or otherwise invalid source/baseline files return exit `3`. Suppression/configuration and output failures return exit `2`; unexpected failures return exit `4`. See [`docs/ERROR-TAXONOMY.md`](docs/ERROR-TAXONOMY.md).

File failures are role-aware: a missing source, baseline, or suppression file is an invocation/configuration error (exit `2`); unreadable, invalid-UTF-8, oversized, malformed, or unsupported source and canonical baseline data is a schema/baseline error (exit `3`); invalid, unreadable, invalid-UTF-8, or oversized suppression data is an invocation/configuration error (exit `2`); and an output write failure is an invocation/configuration error (exit `2`). Only unexpected failures reach exit `4`. Text and JSON output use the same taxonomy.

## Schema validity versus compatibility

`validate` answers whether one document is parseable and supported. `snapshot` creates a versioned canonical representation. `check` and `diff` compare two valid descriptions. A valid schema can still be incompatible with a valid baseline; compatibility is a relationship between two manifests, not a property of one input.
