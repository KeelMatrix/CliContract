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

Breaking changes are gated by default. Use `--fail-on warning` when default, status, or other warning changes should also fail CI. Represented `info` metadata, install guidance, summary/description/help, and choice-description changes produce `KMCLI005` informational findings; they are reported in both modes but do not gate either threshold. Suppressions are explicit JSON files passed with `--ignore`; they never suppress malformed or unsupported schemas.

The stable diagnostic catalog and classification contract are in [`COMPATIBILITY-RULES.md`](COMPATIBILITY-RULES.md). The versioned canonical manifest is described in [`MANIFEST.md`](MANIFEST.md). The accepted invocation-name graph, binary identity, action/group runnable state, positional slots, type and choice domains, argument passthrough behavior, alternative default sources, keyed global file-source configuration, and exit-code contracts are represented there.

Command and option renames remain compatible only when the old invocation name is retained as an alias. A retained group alias also preserves descendant paths. All supported OpenCLI type transitions (`string`, `number`, `integer`, and `boolean`) are checked for removal of accepted lexical values; global and command exit-code changes are reported as warnings. OpenCLI `x-*` extensions are accepted but outside the compatibility decision.

Finite JSON numbers and recognized YAML integer/float spellings are canonicalized by exact numeric value, including exponent forms whose mantissa ends in a dot such as `5.e2`. YAML `.inf` and `.nan` spellings are outside the JSON-number boundary: an explicit `!!float` form is rejected, while an untagged form remains a string.

Command-specific options are enforced: `snapshot` accepts `--input`, `--format`, `--output`, and `--no-telemetry`; `check` accepts `--input`, `--format`, `--baseline`, `--fail-on`, `--ignore`, and `--no-telemetry`; `diff` accepts `--input`, `--format`, `--fail-on`, `--ignore`, and `--no-telemetry`; `validate` accepts only `--input`, `--format`, and `--no-telemetry`. Unsupported combinations return exit code `2` with `UNSUPPORTED_OPTION`.

## Exit codes

| Code | Meaning |
| ---: | --- |
| `0` | Compatible, or no change at the selected failure threshold |
| `1` | A gated compatibility finding was reported |
| `2` | Invalid invocation or configuration |
| `3` | Invalid or unsupported input schema or baseline |
| `4` | Unexpected tool failure |

JSON output always separates `findings` from `errors`. Informational changes remain findings with their stable code and category even when the selected failure threshold returns exit code `0`; a parser or adapter error cannot be represented as a compatible result.

## Privacy and limitations

CliContract uses the published `KeelMatrix.Telemetry` package for one best-effort activation request after a successful comparison with a nonempty baseline. The request passes no schema-derived values; the shared package emits only its bounded platform, tool-version, CI, and anonymous identity contract. `--no-telemetry`, `KEELMATRIX_NO_TELEMETRY=1`, `KEELMATRIX_DEVELOPMENT=true`, and `CI=true` disable the request; development and CI are therefore always telemetry-suppressed. Telemetry failure never changes the comparison result. No schema content, paths, command names, defaults, or diagnostics leave the machine.

The only supported upstream format is OpenCLI `1.0.0-alpha.14`. Unknown format versions, malformed documents, invalid UTF-8, duplicate keys, unknown fields outside `x-*` extensions, YAML anchors/aliases, remote references, and unsupported canonical manifest versions fail closed. Schema validity means the document is well-formed and within the supported contract; it does not mean the document is backward compatible with a baseline.

The OpenCLI adapter status is pinned to `1.0.0-alpha.14`; official examples and the regression corpus are revalidated before each material adapter release. A future upstream version is not accepted until its contract is explicitly reviewed and versioned.

The tool does not execute described CLIs, infer runtime behavior, generate clients or documentation, parse help output, fetch remote references, or provide a hosted registry.

The adapter is intentionally pinned to OpenCLI `1.0.0-alpha.14`. The current upstream specification and tooling have moved forward; the reviewed standards boundary and the .NET CLI-schema/System.CommandLine feasibility decision are recorded in [`docs/OPENCLI-FRESHNESS.md`](docs/OPENCLI-FRESHNESS.md).

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
- `INPUT_UNREADABLE` or `BASELINE_UNREADABLE`: grant read access to the source or canonical baseline; these remain schema/baseline errors with exit code `3`.
- `IGNORE_NOT_FOUND`, `IGNORE_UNREADABLE`, `IGNORE_TOO_LARGE`, or `INVALID_IGNORE`: fix the explicit suppression/configuration file; these are invocation errors with exit code `2`.
- `OUTPUT_NOT_WRITABLE`: choose a writable snapshot destination; this is an invocation error with exit code `2`.
- `OPENCLI_VARIADIC` or `OPENCLI_ARITY`: keep one variadic positional argument last and use item bounds only with `variadic: true`.
- `UNSUPPORTED_OPENCLI_VERSION`: update the source to the pinned OpenCLI version or wait for a tool version that supports it.
- `AMBIGUOUS_INPUT`: pass `--input opencli` after removing competing schema markers.
- `DUPLICATE_OPTION`: provide each command-line option at most once.
- `OPENCLI_DUPLICATE_PARAMETER`: remove duplicate argument or option names after OpenCLI option-name normalization.
- `OPENCLI_DUPLICATE_COMMAND_PATH`: rename one of the command keys so each normalized logical command path is unique.
- `OPENCLI_REMOTE_REFERENCE`: remove the remote reference or include; this tool never resolves it or accesses the network.
- `INVALID_BASELINE` or `BASELINE_VERSION_MISMATCH`: recreate the baseline with `snapshot` from the same supported format version.
- `CANONICALIZATION_FAILED`: inspect the bounded diagnostic and source fields; no baseline is rewritten after a failed `check`.

## License

MIT. See [`LICENSE`](LICENSE).
