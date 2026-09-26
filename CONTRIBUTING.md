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
criterion text and criterion hashes before accepting candidate-SHA proof. Every `MET` proof must contain at least one
of these anchors: a candidate-bound GitHub Actions run, `command=<exact command>; output=<captured output>`,
`checker=<repository-owned scripts/*.ps1 path>; checker_output=<captured result>` (the linter reruns the checker), or
`judgement=artifacts=<one or more existing repository paths>; rationale=<criterion-specific reviewer rationale>`.
`repo_path=<existing repository path>` and `criterion_value=<value>` are supplemental references only; either one by
itself is rejected. Judgement rows are marked `proof_kind=judgement` in the generated map so they are visibly
distinct from reproducible proof. Run references are resolved independently by both scripts with
`gh run view <id> --repo KeelMatrix/CliContract --json headSha,status,conclusion,event`; the linter rejects any
embedded metadata that does not match the live run and candidate SHA. The production generator has no run-metadata
override. Run the permanent script regression with:

```powershell
pwsh -NoProfile -File ./scripts/test-acceptance-map.ps1
```
