# Contributing

## Before You Begin

Install the .NET 8 SDK selected by `global.json`, PowerShell 7 or later, and Git. Restore uses the controlled sources in
`NuGet.config`; restore and the dependency audit require access to NuGet.org.

## Validation

Run the focused tests first:

```powershell
dotnet restore KeelMatrix.CliContract.sln --configfile NuGet.config --nologo
dotnet test tests/KeelMatrix.CliContract.Tests/KeelMatrix.CliContract.Tests.csproj -c Release --no-restore --nologo
```

Run formatting and the Release build:

```powershell
dotnet format KeelMatrix.CliContract.sln --verify-no-changes --no-restore
dotnet build KeelMatrix.CliContract.sln -c Release --no-restore --nologo
```

The repository gate runs the complete local validation sequence, including fixture, determinism, no-execution,
vulnerability, package-inspection, and packed-tool smoke checks:

```powershell
pwsh -NoProfile -File ./scripts/local-gate.ps1
```

To pack and inspect the tool separately:

```powershell
dotnet pack src/KeelMatrix.CliContract.Tool/KeelMatrix.CliContract.Tool.csproj -c Release --no-build --include-symbols --output ./artifacts/packages --nologo
pwsh -NoProfile -File ./scripts/inspect-package.ps1 -PackagePath ./artifacts/packages/KeelMatrix.CliContract.0.1.0.nupkg
pwsh -NoProfile -File ./scripts/package-consumer-smoke.ps1 -PackagePath ./artifacts/packages/KeelMatrix.CliContract.0.1.0.nupkg
```

Keep tests and fixtures bounded, deterministic, and offline after restore. The tool must not execute a described CLI,
fetch remote references, or scrape help output. Report vulnerabilities through [`SECURITY.md`](SECURITY.md).

The acceptance-map scripts are deterministic review tooling. `generate-acceptance-map.ps1` takes the current checklist
and a criterion-keyed review ledger, emits rows in checklist order, and `lint-acceptance-map.ps1` verifies exact
criterion text and criterion hashes before accepting candidate-SHA proof. A `MET` proof must also contain at least one
machine-checkable anchor: `repo_path=<existing repository path>`, `command=<exact command>; output=<exact output>`,
`criterion_value=<criterion-specific value>`, or a GitHub Actions run reference. Run references are resolved with
`gh run view <id> --repo KeelMatrix/CliContract --json headSha,status,conclusion,event`; the generator embeds that
metadata and the linter requires every named run to be completed/success at the candidate SHA. Use `-RunMetadataPath`
only for deterministic offline tests with the same JSON fields. Run the permanent script regression with:

```powershell
pwsh -NoProfile -File ./scripts/test-acceptance-map.ps1
```
