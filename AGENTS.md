# KeelMatrix.CliContract developer guide

## Navigation

- `src/KeelMatrix.CliContract.Core` contains the bounded, offline normalization prototype.
- `src/KeelMatrix.CliContract.Probe` is a disposable command-line harness for Phase 0 evidence.
- `tests/KeelMatrix.CliContract.Tests` contains focused regression tests for both input adapters.
- `fixtures/opencli` contains pinned OpenCLI alpha.14 examples and order/line-ending variants.
- `fixtures/dotnet` contains raw output captured from installed .NET SDKs.
- `docs` contains standards freshness notes, the survival table, and reproducibility evidence.
- `scripts` contains bounded verification scripts; scripts must not execute a described CLI.

## Commands

```text
dotnet restore KeelMatrix.CliContract.sln
dotnet test tests/KeelMatrix.CliContract.Tests/KeelMatrix.CliContract.Tests.csproj -c Release --no-restore
dotnet run --project src/KeelMatrix.CliContract.Probe/KeelMatrix.CliContract.Probe.csproj -c Release --no-restore -- normalize opencli fixtures/opencli/phase0.json output.canonical.json
```

The repository is intentionally not packable in this phase. There is no shipped command surface.

## Invariants

- The prototype accepts only the pinned OpenCLI version and the observed .NET CLI-schema shape.
- Input is untrusted: size, node, depth, collection, and string limits are enforced.
- Normalization is offline and never starts a process, loads a described executable, or fetches a URL.
- Canonical paths are logical command paths such as `root / deploy / --region`.
- Canonical output is invariant to source ordering and line endings.
- Do not add package, release, workflow, icon, or final-tool behavior to this Phase 0 repository.

## Validation escalation

Start with the focused test project. If it passes, run the probe corpus and the determinism/no-execution checks documented in `docs/phase0-evidence.md`. Inspect repository and fixture changes before committing. Keep generated output out of source control unless it is a raw fixture explicitly required as evidence.
