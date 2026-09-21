# KeelMatrix CliContract

KeelMatrix CliContract fails CI when an [OpenCLI](https://opencli.dev/specification) command-line contract changes incompatibly. It normalizes OpenCLI `1.0.0-alpha.14` documents into a deterministic baseline and reports removed commands, options, aliases, optional-to-required changes, arity narrowing, and represented type/domain narrowing.

## Install

```bash
dotnet tool install --global KeelMatrix.CliContract
```

## Quick Start

```bash
clicontract snapshot ./opencli.yaml --output cli-contract.json
clicontract check ./opencli.yaml --baseline cli-contract.json
```

The default input mode is `auto`; use `--input opencli` to require OpenCLI. `auto` recognizes a single OpenCLI shape and fails on ambiguity. No target CLI is started, no help text is scraped, and no network request is made.

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
| `4` | Internal analysis error |

JSON output always separates `findings` from `errors`. A parser or adapter error cannot be represented as a compatible result.

## Privacy and limitations

CliContract is offline after restore. It does not send telemetry in v1: the standalone tool has no stable shared telemetry integration, so no schema content, paths, identifiers, or usage payloads leave the machine. `--no-telemetry` is accepted as an explicit opt-out for the optional telemetry boundary.

The only supported upstream format is OpenCLI `1.0.0-alpha.14`. Unknown format versions, malformed documents, YAML anchors/aliases, remote references, and unsupported canonical manifest versions fail closed. Schema validity means the document is well-formed and within the supported contract; it does not mean the document is backward compatible with a baseline.

The OpenCLI adapter status is pinned to `1.0.0-alpha.14`; official examples and the regression corpus are revalidated before each material adapter release. A future upstream version is not accepted until its contract is explicitly reviewed and versioned.

The tool does not execute described CLIs, infer runtime behavior, generate clients or documentation, parse help output, fetch remote references, or provide a hosted registry.

## CI

Run the check in a build step and preserve the baseline in the application repository:

```bash
clicontract check ./opencli.yaml --baseline ./cli-contract.json --format json
```

## Troubleshooting

- `MALFORMED_JSON` or `MALFORMED_YAML`: fix the source syntax; the tool never prints the whole input.
- `UNSUPPORTED_OPENCLI_VERSION`: update the source to the pinned OpenCLI version or wait for a tool version that supports it.
- `AMBIGUOUS_INPUT`: pass `--input opencli` after removing competing schema markers.
- `INVALID_BASELINE` or `BASELINE_VERSION_MISMATCH`: recreate the baseline with `snapshot` from the same supported format version.
- `CANONICALIZATION_FAILED`: inspect the bounded diagnostic and source fields; no baseline is rewritten after a failed `check`.

## License

MIT. See [`LICENSE`](LICENSE).
