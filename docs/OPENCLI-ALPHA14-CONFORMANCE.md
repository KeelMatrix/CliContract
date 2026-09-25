# OpenCLI alpha.14 conformance coverage

CliContract accepts only OpenCLI `1.0.0-alpha.14`. The committed fixtures and tests below are checked against the tagged upstream validation and codec behavior. The upstream source is the `spec/v1.0.0-alpha.14` tag.

## Tagged validation-test inventory

| Tagged test | CliContract coverage |
| --- | --- |
| `TestValidateJSON` | `PinnedAlpha14ConformanceCorpusMatchesItsOracle`; `petstore-cli.ocs.json` |
| `TestValidateYAML` | `TaggedAlpha14CommandKeysStripAllModifierGrammar`; `petstore-cli.ocs.yaml`; `pleasantries-cli.ocs.yaml` |
| `TestValidateJSON_InvalidJSON` | malformed JSON cases in `NormalizationTests` |
| `TestValidateYAML_LogicalValidationErrors` | corpus cases for variadic placement, arity, argument order, group fields, typed defaults, and declared-type choice validation |
| `TestValidateYAML_InvalidYAML` | malformed YAML and unsupported-tag cases in `NormalizationTests` |
| `TestValidationError_PathFormatting` | bounded stable error-code assertions in the normalization tests |
| `TestValidateYAML_DuplicateFlagNames` | duplicate primary and accepted option-name tests |
| `TestValidateYAML_GroupCommandWithArgs` | `TaggedAlpha14LogicalValidationRejectsCompleteRuleFamilies` |
| `TestValidateYAML_ArgMaxItemsWithoutVariadic` | corpus `nonvariadic-bounds` |
| `TestValidateYAML_MinItemsGreaterThanMaxItems` | corpus `reversed-bounds` |
| `TestValidateYAML_DuplicateFlagAliasAcrossFlags` | corpus `duplicate-flag-alias` |
| `TestValidateYAML_FlagMinItemsWithoutVariadic` | corpus `nonvariadic-bounds` |
| `TestValidateYAML_FlagMaxItemsWithoutVariadic` | corpus `nonvariadic-bounds` |
| `TestValidateYAML_FlagMinItemsGreaterThanMaxItems` | corpus `reversed-bounds` and arity tests |
| `TestValidateJSON_InvalidSchema` | committed invalid-schema fixtures and corpus schema-shape cases |
| `TestValidateYAML_SchemaValidationErrorFormatting` | stable bounded diagnostic-code assertions |
| `TestValidateJSON_UnmarshalError` | malformed JSON input tests |

No tagged validation case is omitted. Cases that the tagged validator expresses through one shared cross-field check are represented once in the corpus with the applicable field locations covered by targeted tests.

## Corpus coverage

`fixtures/opencli/alpha14-conformance-corpus.json` covers valid and rejected JSON documents for schema fields, extensions, command identity, command-key modifiers, groups, positional ordering, variadic arguments and flags, duplicate accepted option names, typed defaults, declared-type constrained choices, `$ENV`/`$FILE` prerequisites, exact numeric values, unknown versions, and offline reference rejection. The committed `numeric-integer-domain-fraction.json` and `.yaml` fixtures prove that a fractional choice on an integer parameter is rejected with bounded `OPENCLI_CHOICE` exit-`3` behavior in both source formats. JSON/YAML byte-equivalence and self-reflexive canonical round trips are checked separately using the numeric, petstore, pleasantries, and regression fixtures.
`fixtures/opencli/round19-source-producibility.json` proves that command-key suffixes beginning with non-ASCII-letter syntax terminate parsing without creating an impossible canonical segment, while contact and install partial metadata states remain representable.

The extension case deliberately contains `$ref`, `$dynamicRef`, and `include` inside an opaque `x-*` subtree. Those values are metadata and are never interpreted or fetched.

## Canonical semantic families

The alpha.14 codec's command-key trie is represented explicitly in canonical schema version `2`. Missing ancestors and a missing root are materialized as derived `group` nodes; explicit group declarations are equivalent to their derived form, while an explicit action becoming a derived group is a callable-surface break. Parent aliases and descendant paths are preserved through the materialized trie.

`global.flags` are stored as `GlobalOptions`, separate from root-command-local `Root.Options`. Compatibility compares inherited global options plus each command's local options at every command path. Scope moves exercise option add/remove behavior and all represented option attributes, and collisions between inherited and local accepted names are rejected.

The representative source fixture [`fixtures/opencli/fix-round17-scope-and-trie.json`](../fixtures/opencli/fix-round17-scope-and-trie.json) combines an inherited global option, an explicit aliased group, a derived root, and a descendant action. The regression script snapshots and self-compares it as part of the fixture corpus.

`CanonicalManifestReader` enforces the complete source-version-aware invariant before comparison: every accepted canonical manifest must be a state the supported alpha.14 normalizer could legitimately produce. Hostile but valid JSON canonical baselines for duplicate source formats or exit codes, empty global-config wrappers, invalid paths and hierarchy, invalid sources, argument-only fields, unsupported status, requiredness-inconsistent non-variadic arity, invalid variadic required/min/max combinations, invalid positional ordering or variadic placement, reversed choices, malformed names, contradictory domains, and global/local collisions are bounded baseline errors (exit `3`), never unexpected failures.

## Source-producibility matrix

The following matrix records the alpha.14 source rule, its canonical enforcement point, and the negative regression that must remain bounded at exit `3`. The contract is one-way: every accepted canonical state is source-producible; canonical fields that collapse an omitted source property into an empty collection remain intentionally representable.

| Source rule | Canonical enforcement | Negative regression |
| --- | --- | --- |
| `info.title`, `info.binary`, and `info.version` are required and nonempty; nonempty whitespace is preserved | `CanonicalInvariantValidator.Validate` | `Alpha14WhitespaceStringsRemainSourceProducibleAndRoundTrip`, `Alpha14WhitespaceBinaryRemainsSourceProducible`, and `missing-info-title` |
| A contact has at least one of `name`, `email`, or `url` | `CanonicalInvariantValidator.ValidateInfo` | `CanonicalManifestReaderRejectsEveryContactAnyOfVariant` / `contact-all-null`; installed `contact-all-null-check` and `contact-all-null-diff` |
| Each install method has a nonempty `name` and a `command` or `url`; nonempty whitespace is preserved | `CanonicalInvariantValidator.ValidateInfo` | `Alpha14WhitespaceStringsRemainSourceProducibleAndRoundTrip` and `install-without-command-or-url`; installed counterpart |
| License name and example content are required and nonempty; nonempty whitespace is preserved | `CanonicalInvariantValidator.ValidateInfo` and `ValidateExamples` | `Alpha14WhitespaceStringsRemainSourceProducibleAndRoundTrip`, hostile reader cases `license-empty-name` and `empty-example-content` |
| Command keys stop before a whitespace-delimited non-ASCII-letter token; canonical segments start with an ASCII letter and retain materialized parents | `CanonicalInvariantValidator.ValidateCommandTree` / `ValidateCommandPath` | `CanonicalManifestReaderRejectsCommandSegmentsAlpha14CannotProduce`; installed `command-path-*` check/diff cases |
| Root and group records obey the flat materialized trie and groups have no local args/flags | `ValidateCommandTree` and `ValidateCommand` | hostile reader `invalid-command-path` and `group-local-option` |
| Non-variadic and variadic requiredness/arity, positional ordering, and variadic placement follow alpha.14 | `ValidateArgumentCollection`, `ValidateParameter` | `CanonicalManifestReaderEnforcesOptionArityMatrix`, `CanonicalManifestReaderEnforcesArgumentArityMatrix`, `CanonicalManifestReaderRejectsArgumentOrderingAndVariadicPlacement` |
| Global file-source formats are unique, supported, sorted, and nonempty; a non-null config has a source | `ValidateFileSources` | hostile `duplicate-file-format`, `empty-file-path`, and `CanonicalManifestReaderRejectsEmptyGlobalConfigWrapper` |
| Exit-code values are unique, statuses are from alpha.14, summaries are nonempty (including whitespace-only summaries), and values are sorted | `ValidateExitCodes` | `Alpha14WhitespaceStringsRemainSourceProducibleAndRoundTrip`, hostile `duplicate-global-exit-code`, `duplicate-command-exit-code`, and `invalid-exit-status` |
| `$ENV`/`$FILE` sources use supported types and nonempty properties; nonempty whitespace properties are preserved; `$FILE` needs global file configuration | `ValidateSources` | `Alpha14WhitespaceStringsRemainSourceProducibleAndRoundTrip`, hostile `invalid-source-type`, `empty-source-property`, and `file-source-without-config` |
| Arguments cannot carry flag-only defaults or alternative sources | `ValidateParameter` | hostile `argument-default` |
| Commands and parameters cannot inject alpha.14-unrepresentable status/deprecation state | `ValidateCommand` and `ValidateParameter` | hostile `unrepresentable-status` |
| Option types are exact alpha.14 values; names use the normalizer's `--` + `TrimStart('-')` construction (including whitespace, Unicode, punctuation, and dash-only names that produce `--`); aliases are nonempty, duplicate-free collections and preserve whitespace | shared source-contract rules used by normalization and `ValidateOptionCollection` | `Alpha14OptionNamesPreserveWhitespaceAndTrimmedDashOnlyFormsAtEveryScope`, `Alpha14DashesOnlyOptionNamesAndAliasesRemainSourceProducibleAtEveryScope`, `SourceProducibilityGuardTests`, and hostile `invalid-option-name` / `invalid-option-alias` |
| `AllowedValues` exactly mirrors `Choices[].Value`, choices use scalar sort order, and values match the declared type | `ValidateAllowedValues` and `ValidateChoices` | hostile `contradictory-domain`, `reversed-choice-order`, and `CanonicalManifestReaderRejectsNonIntegralIntegerChoices` |
| An omitted alternative-source/choice collection serializes as the same canonical empty collection as an absent source property | canonical representation | No restriction: this state is produced by omission and cannot retain source-property presence |

The closed source-producibility guard is runnable as one command:
`pwsh -NoProfile -File ./scripts/source-producibility-guard.ps1`. It executes the table-driven edge matrix, then installs the freshly packed tool and exercises source `validate`/`snapshot` plus canonical `check`/`diff` round trips and the bounded hostile rejection set.
