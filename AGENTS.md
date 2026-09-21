# KeelMatrix.CliContract developer guide

## Navigation

- `src/KeelMatrix.CliContract.Core` contains the bounded, offline canonical model, OpenCLI adapter, normalizer, and compatibility engine.
- `src/KeelMatrix.CliContract.Probe` is a disposable normalization harness for regression evidence.
- `src/KeelMatrix.CliContract.Tool` is the packable `net8.0` `clicontract` tool.
- `tests/KeelMatrix.CliContract.Tests` contains focused regression tests for normalization, compatibility, parser limits, and the OpenCLI adapter.
- `fixtures/opencli` contains pinned OpenCLI alpha.14 examples and authored regression fixtures.
- `fixtures/dotnet` contains .NET CLI-schema captures used as Phase 0 evidence; they are not a supported v1 adapter.
- `docs` contains standards freshness notes and reproducibility evidence.
- `scripts` contains the deterministic validation, package inspection, and packed-tool smoke gates.

## Commands

```text
dotnet restore KeelMatrix.CliContract.sln
dotnet test tests/KeelMatrix.CliContract.Tests/KeelMatrix.CliContract.Tests.csproj -c Release --no-restore
dotnet format KeelMatrix.CliContract.sln --verify-no-changes --no-restore
dotnet run --project src/KeelMatrix.CliContract.Probe/KeelMatrix.CliContract.Probe.csproj -c Release --no-restore -- normalize opencli fixtures/opencli/phase0.json output.canonical.json
dotnet build KeelMatrix.CliContract.sln -c Release --no-restore
pwsh -NoProfile -File ./scripts/local-gate.ps1
```

The shipping command surface is OpenCLI-only: `--input auto|opencli`. The tool project is the only packable project; core, probe, tests, and fixtures are not separate package products.

## Invariants

- The shipping tool accepts only OpenCLI `1.0.0-alpha.14`. The `.NET CLI-schema` captures remain regression evidence and are not a v1 adapter.
- Input is untrusted: size, node, depth, collection, and string limits are enforced.
- Normalization is offline and never starts a process, loads a described executable, or fetches a URL.
- Canonical paths are logical command paths such as `root / deploy / --region`.
- Canonical output is invariant to source ordering and line endings.
- Unknown source and canonical manifest versions fail closed.
- The packed tool must contain all required internal assemblies and pass the isolated consumer smoke test.
- Do not create, copy, modify, or delete icon bytes; the required icon is founder-owned and resolved by the pack configuration.

## Validation escalation

Start with the focused test project. If it passes, run the local gate, inspect the actual package, and run the clean packed-tool consumer smoke test. Keep generated output out of source control unless it is a raw fixture explicitly required as evidence.
