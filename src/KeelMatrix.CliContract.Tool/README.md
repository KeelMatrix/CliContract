# KeelMatrix.CliContract

KeelMatrix.CliContract is a .NET tool that detects incompatible changes in OpenCLI command-line contracts.

## Install

```bash
dotnet tool install --global KeelMatrix.CliContract
```

## Requirements

CliContract targets `net8.0` and requires the .NET 8 runtime. It is intended to run on Windows, Linux, and macOS where .NET 8 is available.

Supported input: OpenCLI `1.0.0-alpha.14`, in JSON or YAML. Schema parsing and comparison require no network after restore; a successful comparison may make one bounded best-effort telemetry request unless telemetry is disabled. `CI=true` and `KEELMATRIX_DEVELOPMENT=true` suppress telemetry automatically. The tool does not execute the described CLI. The adapter remains intentionally pinned while upstream advances; the reviewed standards boundary is recorded in the repository's `docs/OPENCLI-FRESHNESS.md`.

## Quick Start

```bash
clicontract snapshot ./opencli.yaml --output cli-contract.json
clicontract check ./opencli.yaml --baseline cli-contract.json
```

Use `--format json` for CI consumers, `--fail-on warning` to gate warnings, and `--ignore rules.json` for explicit reviewed suppressions. Represented `info` metadata, install guidance, summary/description text, and choice-description changes are emitted as non-gating `KMCLI005` findings in the JSON `findings` array; visibility and example metadata changes are emitted as non-gating `KMCLI006` findings. The versioned canonical manifest schema is `2`: global flags remain distinct from root-local flags, inherited global options are compared at every command, and missing command ancestors are materialized as derived non-runnable groups. The accepted invocation-name graph, binary identity, action/group runnable state, positional slots, all supported type and choice domains, argument passthrough behavior, default-source precedence, keyed global file-source configuration, and global/command exit-code contracts are compared as described in the [compatibility rules](https://github.com/KeelMatrix/CliContract/blob/main/COMPATIBILITY-RULES.md), [manifest specification](https://github.com/KeelMatrix/CliContract/blob/main/MANIFEST.md), and [alpha.14 conformance matrix](https://github.com/KeelMatrix/CliContract/blob/main/docs/OPENCLI-ALPHA14-CONFORMANCE.md). Retaining an old command or option name as an alias preserves that invocation, including descendants below a renamed group. Canonical baselines are accepted only when the alpha.14 normalizer could produce them: required metadata and contact/install presence rules, nonempty whitespace-only source strings preserved verbatim, the shared `--` + `TrimStart('-')` option-name construction rule, ASCII-letter command segments, exact option types, non-variadic requiredness determines `0..1` or `1..1`, variadic bounds obey the required/variadic/min/max matrix, scalar choices are ordered canonically, and a non-null global config contains a file source. Empty required strings, empty aliases, and duplicate aliases remain invalid. Finite JSON numbers and recognized YAML integer/float spellings are canonicalized by exact numeric value, including trailing-dot exponent mantissas such as `5.e2`. Choices and defaults must match their declared types; fractional integer choices and other typed mismatches fail closed with exit code `3` (`OPENCLI_CHOICE` or `OPENCLI_DEFAULT`). The same exact numeric representation is reused by validation and compatibility comparison, so accepted manifests compare cleanly with themselves. YAML `.inf` and `.nan` are outside the JSON-number boundary: explicit `!!float` forms are rejected, while untagged forms remain strings. Unsupported command-option combinations return exit code `2` with `UNSUPPORTED_OPTION`; see `clicontract --help` for the per-command option matrix.

## Exit codes

`0` means no gated finding, `1` means a gated finding, `2` means a missing source/baseline path, invalid invocation/configuration, or output failure, `3` means a present but unreadable or invalid source/baseline (including an impossible canonical manifest), and `4` means an unexpected tool failure. See the [error taxonomy](https://github.com/KeelMatrix/CliContract/blob/main/docs/ERROR-TAXONOMY.md). JSON output places compatibility findings and tool errors in separate arrays.

When package behavior is cited in a repository acceptance record, use a candidate-bound CI run, an exact command with captured output, a rerunnable checker, or an explicit judgement that names the relevant artifact and rationale. A package, fixture, or manifest path alone is not a proof anchor; the repository acceptance-map linter rejects path-only proof. The production acceptance-map path independently validates named CI runs; its self-test uses internal fixture metadata and requires no `gh`, network access, or `GH_TOKEN`.

## Privacy and limitations

After a successful comparison with a nonempty baseline, version 0.1 requests one best-effort activation through the published `KeelMatrix.Telemetry` package. No schema-derived values are passed; the shared package's bounded platform, tool-version, CI, and anonymous identity contract is used. `--no-telemetry`, `KEELMATRIX_NO_TELEMETRY=1`, `KEELMATRIX_DEVELOPMENT=true`, and `CI=true` disable the request; development and CI are always telemetry-suppressed, and telemetry failure cannot affect the result. The tool never sends schema contents, defaults, paths, command names, or diagnostics. Unknown versions, malformed input, invalid UTF-8, YAML aliases, remote references, duplicate normalized parameter names, duplicate normalized command paths, and unsupported constructs fail closed. A schema can be valid without being backward compatible.

## License

MIT.
