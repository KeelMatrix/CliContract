# KeelMatrix.CliContract

KeelMatrix.CliContract is a .NET tool that detects incompatible changes in OpenCLI command-line contracts.

## Install

```bash
dotnet tool install --global KeelMatrix.CliContract
```

Supported input: OpenCLI `1.0.0-alpha.14`, in JSON or YAML. The tool is offline after restore and does not execute the described CLI.

## Quick Start

```bash
clicontract snapshot ./opencli.yaml --output cli-contract.json
clicontract check ./opencli.yaml --baseline cli-contract.json
```

Use `--format json` for CI consumers, `--fail-on warning` to gate warnings, and `--ignore rules.json` for explicit reviewed suppressions. See the [compatibility rules](https://github.com/KeelMatrix/CliContract/blob/main/COMPATIBILITY-RULES.md) and [manifest specification](https://github.com/KeelMatrix/CliContract/blob/main/MANIFEST.md).

## Exit codes

`0` means no gated finding, `1` means a gated finding, `2` means invalid invocation/configuration, `3` means invalid or unsupported input, and `4` means an internal analysis error. JSON output places compatibility findings and tool errors in separate arrays.

## Privacy and limitations

Version 0.1 emits no telemetry because the standalone tool has no stable shared telemetry integration. It never sends schema contents, defaults, paths, or identifiers. `--no-telemetry` explicitly disables the optional telemetry boundary. Unknown versions, malformed input, YAML aliases, remote references, and unsupported constructs fail closed. A schema can be valid without being backward compatible.

## License

MIT.
