# OpenCLI alpha.14 conformance coverage

CliContract accepts only OpenCLI `1.0.0-alpha.14`. The committed fixtures and tests below are checked against the tagged upstream validation and codec behavior. The upstream source is the `spec/v1.0.0-alpha.14` tag.

## Tagged validation-test inventory

| Tagged test | CliContract coverage |
| --- | --- |
| `TestValidateJSON` | `PinnedAlpha14ConformanceCorpusMatchesItsOracle`; `petstore-cli.ocs.json` |
| `TestValidateYAML` | `TaggedAlpha14CommandKeysStripAllModifierGrammar`; `petstore-cli.ocs.yaml`; `pleasantries-cli.ocs.yaml` |
| `TestValidateJSON_InvalidJSON` | malformed JSON cases in `NormalizationTests` |
| `TestValidateYAML_LogicalValidationErrors` | corpus cases for variadic placement, arity, argument order, group fields, and typed defaults |
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

`fixtures/opencli/alpha14-conformance-corpus.json` covers valid and rejected JSON documents for schema fields, extensions, command identity, command-key modifiers, groups, positional ordering, variadic arguments and flags, duplicate accepted option names, typed defaults, `$ENV`/`$FILE` prerequisites, exact numeric values, unknown versions, and offline reference rejection. JSON/YAML byte-equivalence is checked separately using the tagged petstore and pleasantries fixtures.

The extension case deliberately contains `$ref`, `$dynamicRef`, and `include` inside an opaque `x-*` subtree. Those values are metadata and are never interpreted or fetched.
