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

The extension case deliberately contains `$ref`, `$dynamicRef`, and `include` inside an opaque `x-*` subtree. Those values are metadata and are never interpreted or fetched.

## Canonical semantic families

The alpha.14 codec's command-key trie is represented explicitly in canonical schema version `2`. Missing ancestors and a missing root are materialized as derived `group` nodes; explicit group declarations are equivalent to their derived form, while an explicit action becoming a derived group is a callable-surface break. Parent aliases and descendant paths are preserved through the materialized trie.

`global.flags` are stored as `GlobalOptions`, separate from root-command-local `Root.Options`. Compatibility compares inherited global options plus each command's local options at every command path. Scope moves exercise option add/remove behavior and all represented option attributes, and collisions between inherited and local accepted names are rejected.

The representative source fixture [`fixtures/opencli/fix-round17-scope-and-trie.json`](../fixtures/opencli/fix-round17-scope-and-trie.json) combines an inherited global option, an explicit aliased group, a derived root, and a descendant action. The regression script snapshots and self-compares it as part of the fixture corpus.

`CanonicalManifestReader` enforces the complete source-version-aware invariant before comparison: every accepted canonical manifest must be a state the supported alpha.14 normalizer could legitimately produce. Hostile but valid JSON canonical baselines for duplicate source formats or exit codes, empty global-config wrappers, invalid paths and hierarchy, invalid sources, argument-only fields, unsupported status, requiredness-inconsistent non-variadic arity, invalid variadic required/min/max combinations, invalid positional ordering or variadic placement, reversed choices, malformed names, contradictory domains, and global/local collisions are bounded baseline errors (exit `3`), never unexpected failures.
