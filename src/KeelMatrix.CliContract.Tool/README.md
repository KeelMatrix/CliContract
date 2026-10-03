# KeelMatrix.CliContract

KeelMatrix.CliContract is a .NET tool that detects incompatible changes in OpenCLI command-line contracts.

## Install

```bash
dotnet tool install --global KeelMatrix.CliContract
```

## Requirements

CliContract targets `net8.0` and requires the .NET 8 runtime. It is intended to run on Windows, Linux, and macOS where .NET 8 is available.

Supported input: OpenCLI `1.0.0-alpha.14`, in JSON or YAML. Schema parsing and comparison require no network after restore; a successful comparison whose canonical baseline contains a runnable action at the root or below a group may make one bounded best-effort telemetry request unless telemetry is disabled. Group-only commands and cosmetic metadata never qualify. `CI=true` and `KEELMATRIX_DEVELOPMENT=true` suppress telemetry automatically. The tool does not execute the described CLI. The adapter remains intentionally pinned while upstream advances; the reviewed standards boundary is recorded in the repository's `docs/OPENCLI-FRESHNESS.md`.

## Quick Start

```bash
clicontract snapshot ./opencli.yaml --output cli-contract.json
clicontract check ./opencli.yaml --baseline cli-contract.json
```

Use `--format json` for CI consumers, `--fail-on warning` to gate warnings, and `--ignore rules.json` for explicit reviewed suppressions. Represented `info` metadata, install guidance, summary/description text, and choice-description changes are emitted as non-gating `KMCLI005` findings in the JSON `findings` array; visibility and example metadata changes are emitted as non-gating `KMCLI006` findings. The versioned canonical manifest schema is `2`: global flags remain distinct from root-local flags, inherited global options are compared at every command, and missing command ancestors are materialized as derived non-runnable groups. The accepted invocation-name graph, binary identity, action/group runnable state, positional slots, all supported type and choice domains, argument passthrough behavior, default-source precedence, keyed global file-source configuration, and global/command exit-code contracts are compared as described in the [compatibility rules](https://github.com/KeelMatrix/CliContract/blob/main/COMPATIBILITY-RULES.md), [manifest specification](https://github.com/KeelMatrix/CliContract/blob/main/MANIFEST.md), and [alpha.14 conformance matrix](https://github.com/KeelMatrix/CliContract/blob/main/docs/OPENCLI-ALPHA14-CONFORMANCE.md). The exact upstream source record for the option-name boundary is in the [alpha.14 option-name oracle](https://github.com/KeelMatrix/CliContract/blob/main/docs/OPENCLI-ALPHA14-OPTION-NAME-ORACLE.md). Retaining an old command or option name as an alias preserves that invocation, including descendants below a renamed group. Canonical baselines are accepted only when the alpha.14 normalizer could produce them: required metadata and contact/install presence rules, nonempty whitespace-only source strings preserved verbatim, primary option names canonically use `--` plus the accepted source name, and empty option names or aliases, names beginning with `-` or `=`, and alias lists with multiple single-byte aliases fail with `OPENCLI_OPTION_NAME`. Alpha.14 selects the first single-byte flag alias as shorthand before the canonical schema sorts aliases; the shorthand is `-x` and other aliases are `--alias`. Command aliases remain whole command names. Other canonical constraints include ASCII-letter command segments, exact option types, non-variadic requiredness-derived `0..1` or `1..1` arity, variadic bounds obeying the required/variadic/min/max matrix, scalar choices ordered canonically, and a non-null global config containing a file source. Empty required strings, empty aliases, and duplicate aliases remain invalid. Finite JSON numbers and recognized YAML integer/float spellings are canonicalized by exact numeric value, including trailing-dot exponent mantissas such as `5.e2`. Choices and defaults must match their declared types; fractional integer choices and other typed mismatches fail closed with exit code `3` (`OPENCLI_CHOICE` or `OPENCLI_DEFAULT`). The same exact numeric representation is reused by validation and compatibility comparison, so accepted manifests compare cleanly with themselves. YAML `.inf` and `.nan` are outside the JSON-number boundary: explicit `!!float` forms are rejected, while untagged forms remain strings. Unsupported command-option combinations return exit code `2` with `UNSUPPORTED_OPTION`; see `clicontract --help` for the per-command option matrix.

Input selection uses one role contract for every verb. `--input opencli` requires OpenCLI source for every source-schema operand and rejects canonical manifests. `diff --input auto` accepts source/source, canonical/canonical, and mixed operand pairs in either order; canonical input is not an undocumented explicit-OpenCLI mode. `snapshot`, `validate`, and the source operand of `check` reject canonical input, while `check --baseline` always reads a canonical manifest. Malformed or ambiguous canonical-like JSON fails closed.

The alpha.14 command-key delimiter class is Go's `[^\S\r\n]`: ASCII space, tab, and form feed only. CR, LF, vertical tab, NBSP, em-space, and narrow NBSP are not delimiters; modifier boundaries use that same class and require a following non-ASCII-letter character. The pinned split preserves empty edge segments. Since canonical schema version 2 cannot represent an empty root or command segment, every pinned derivation with an empty edge segment is rejected before canonical path parsing with `OPENCLI_COMMAND_KEY`, exit `3`, and the stable diagnostic `OpenCLI command keys with pinned whitespace-edge segments are unsupported because the canonical root and command segments must be nonempty.`

Text output escapes user-derived control and format characters, bidi overrides and isolates, zero-width characters, line separators, C0/C1 controls, and workflow-command marker delimiters at the final diagnostic boundary.

## Exit codes

`0` means no gated finding, `1` means a gated finding, `2` means a missing source/baseline path, invalid invocation/configuration, or output failure, `3` means a present but unreadable or invalid source/baseline (including an impossible canonical manifest), and `4` means an unexpected tool failure. See the [error taxonomy](https://github.com/KeelMatrix/CliContract/blob/main/docs/ERROR-TAXONOMY.md). JSON output places compatibility findings and tool errors in separate arrays.

## Privacy and limitations

After a successful comparison whose canonical baseline contains a runnable action at the root or below a group, version 0.1 requests one best-effort activation through the published `KeelMatrix.Telemetry` package. Group-only commands and cosmetic metadata never qualify. No schema-derived values are passed; the shared package's bounded platform, tool-version, CI, and anonymous identity contract is used. `--no-telemetry`, `KEELMATRIX_NO_TELEMETRY=1`, `KEELMATRIX_DEVELOPMENT=true`, and `CI=true` disable the request; development and CI are always telemetry-suppressed, and telemetry failure cannot affect the result. The tool never sends schema contents, defaults, paths, command names, or diagnostics. Unknown versions, malformed input, invalid UTF-8, YAML aliases, remote references, invalid option spellings, duplicate normalized parameter names, duplicate normalized command paths, and unsupported constructs fail closed. A schema can be valid without being backward compatible.

Analysis is bounded by input size, node/depth/string/collection, materialized-command, derived-invocation count and aggregate-character, comparison-work, and canonical-output limits. Derived invocation budgets are reserved before concatenated paths are allocated. These limits fail closed with exit code `3`; a successful snapshot is admitted by the same canonical-reader limits used by `check` and `diff`.

## License

MIT.
