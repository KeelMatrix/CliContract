# KeelMatrix.CliContract

KeelMatrix.CliContract is a .NET tool that detects incompatible changes in OpenCLI command-line contracts.

## Install

```bash
dotnet tool install --global KeelMatrix.CliContract
```

## Requirements

CliContract targets `net8.0` and requires the .NET 8 runtime. It is intended to run on Windows, Linux, and macOS where .NET 8 is available.

Supported input: OpenCLI `1.0.0-alpha.14`, in JSON or YAML. Schema parsing and comparison require no network after restore; a successful comparison may make one bounded best-effort telemetry request unless telemetry is disabled. `CI=true` and `KEELMATRIX_DEVELOPMENT=true` suppress telemetry automatically. The tool does not execute the described CLI.

## Quick Start

```bash
clicontract snapshot ./opencli.yaml --output cli-contract.json
clicontract check ./opencli.yaml --baseline cli-contract.json
```

Use `--format json` for CI consumers, `--fail-on warning` to gate warnings, and `--ignore rules.json` for explicit reviewed suppressions. See the [compatibility rules](https://github.com/KeelMatrix/CliContract/blob/main/COMPATIBILITY-RULES.md) and [manifest specification](https://github.com/KeelMatrix/CliContract/blob/main/MANIFEST.md).

## Exit codes

`0` means no gated finding, `1` means a gated finding, `2` means invalid invocation/configuration, `3` means invalid or unsupported input, and `4` means an unexpected tool failure. JSON output places compatibility findings and tool errors in separate arrays.

## Privacy and limitations

After a successful comparison with a nonempty baseline, version 0.1 requests one best-effort activation through the published `KeelMatrix.Telemetry` package. No schema-derived values are passed; the shared package's bounded platform, tool-version, CI, and anonymous identity contract is used. `--no-telemetry`, `KEELMATRIX_NO_TELEMETRY=1`, `KEELMATRIX_DEVELOPMENT=true`, and `CI=true` disable the request; development and CI are always telemetry-suppressed, and telemetry failure cannot affect the result. The tool never sends schema contents, defaults, paths, command names, or diagnostics. Unknown versions, malformed input, invalid UTF-8, YAML aliases, remote references, and unsupported constructs fail closed. A schema can be valid without being backward compatible.

## License

MIT.
