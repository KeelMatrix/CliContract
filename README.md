# KeelMatrix CliContract

KeelMatrix CliContract fails CI when an [OpenCLI](https://opencli.dev/specification) command-line contract changes incompatibly. It normalizes OpenCLI `1.0.0-alpha.14` documents into a deterministic baseline and reports removed commands, options, aliases, optional-to-required changes, arity narrowing, and represented type/domain narrowing.

## Install

```bash
dotnet tool install --global KeelMatrix.CliContract
```

## Requirements

CliContract targets `net8.0` and requires the .NET 8 runtime. It is intended to run on Windows, Linux, and macOS where .NET 8 is available.

## Quick Start

```bash
clicontract snapshot ./opencli.yaml --output cli-contract.json
git add cli-contract.json
git commit -m "Record CLI contract baseline"
clicontract check ./opencli.yaml --baseline cli-contract.json
```

Commit the baseline with the CLI description, then run `check` in CI. The default input mode is `auto`; use `--input opencli` to require OpenCLI. `auto` recognizes a single OpenCLI shape and fails on ambiguity. No target CLI is started and no help text is scraped. Schema parsing and comparison require no network connection; after a successful comparison with a nonempty baseline, one bounded best-effort activation request may be sent through `KeelMatrix.Telemetry` unless telemetry is disabled. The request is automatically suppressed for `CI=true` and `KEELMATRIX_DEVELOPMENT=true` runs.

```bash
clicontract diff ./old-opencli.yaml ./new-opencli.yaml --format json
clicontract validate ./opencli.yaml
```

## Compatibility rules

Breaking changes are gated by default. Use `--fail-on warning` when default or status changes should also fail CI. Description/help changes are informational. Suppressions are explicit JSON files passed with `--ignore`; they never suppress malformed or unsupported schemas.

The stable diagnostic catalog and classification contract are in [`COMPATIBILITY-RULES.md`](COMPATIBILITY-RULES.md). The versioned canonical manifest is described in [`MANIFEST.md`](MANIFEST.md).

## Exit codes

| Code | Meaning |
| ---: | --- |
| `0` | Compatible, or no change at the selected failure threshold |
| `1` | A gated compatibility finding was reported |
| `2` | Invalid invocation or configuration |
| `3` | Invalid or unsupported input schema or baseline |
| `4` | Unexpected tool failure |

JSON output always separates `findings` from `errors`. A parser or adapter error cannot be represented as a compatible result.

## Privacy and limitations

CliContract uses the published `KeelMatrix.Telemetry` package for one best-effort activation request after a successful comparison with a nonempty baseline. The request passes no schema-derived values; the shared package emits only its bounded platform, tool-version, CI, and anonymous identity contract. `--no-telemetry`, `KEELMATRIX_NO_TELEMETRY=1`, `KEELMATRIX_DEVELOPMENT=true`, and `CI=true` disable the request; development and CI are therefore always telemetry-suppressed. Telemetry failure never changes the comparison result. No schema content, paths, command names, defaults, or diagnostics leave the machine.

The only supported upstream format is OpenCLI `1.0.0-alpha.14`. Unknown format versions, malformed documents, invalid UTF-8, duplicate keys, unknown fields outside `x-*` extensions, YAML anchors/aliases, remote references, and unsupported canonical manifest versions fail closed. Schema validity means the document is well-formed and within the supported contract; it does not mean the document is backward compatible with a baseline.

The OpenCLI adapter status is pinned to `1.0.0-alpha.14`; official examples and the regression corpus are revalidated before each material adapter release. A future upstream version is not accepted until its contract is explicitly reviewed and versioned.

The tool does not execute described CLIs, infer runtime behavior, generate clients or documentation, parse help output, fetch remote references, or provide a hosted registry.

## CI

Run the check in a build step and preserve the baseline in the application repository:

```bash
clicontract check ./opencli.yaml --baseline ./cli-contract.json --format json
```

## Troubleshooting

- `MALFORMED_JSON` or `MALFORMED_YAML`: fix the source syntax; the tool never prints the whole input.
- `INPUT_TOO_LARGE`: reduce the input below the configured size limit.
- `DEPTH_LIMIT`: reduce the input nesting below the configured depth limit.
- `INVALID_UTF8`: save the source as strict UTF-8 without a malformed byte sequence.
- `UNSUPPORTED_OPENCLI_VERSION`: update the source to the pinned OpenCLI version or wait for a tool version that supports it.
- `AMBIGUOUS_INPUT`: pass `--input opencli` after removing competing schema markers.
- `DUPLICATE_OPTION`: provide each command-line option at most once.
- `OPENCLI_DUPLICATE_PARAMETER`: remove duplicate argument or option names after OpenCLI option-name normalization.
- `OPENCLI_REMOTE_REFERENCE`: remove the remote reference or include; this tool never resolves it or accesses the network.
- `INVALID_BASELINE` or `BASELINE_VERSION_MISMATCH`: recreate the baseline with `snapshot` from the same supported format version.
- `CANONICALIZATION_FAILED`: inspect the bounded diagnostic and source fields; no baseline is rewritten after a failed `check`.

## License

MIT. See [`LICENSE`](LICENSE).
