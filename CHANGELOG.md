# Changelog

This file records user-visible changes to KeelMatrix.CliContract.

## [Unreleased]

## [0.1.0] - 2026-11-14

### Added

- Provides the .NET 8 `clicontract` tool to validate OpenCLI `1.0.0-alpha.14` JSON/YAML descriptions, create deterministic canonical baselines with `snapshot`, and compare contracts with `check` or `diff`.
- Detects incompatible command, option, and positional-argument changes, including lost invocation names or aliases, requiredness and arity changes, and narrowing of represented types or accepted values.
- Supports CI gates with stable text/JSON diagnostics, documented exit codes, configurable failure thresholds, and explicit finding suppressions that cannot hide invalid inputs.
- Processes descriptions locally with bounded parsing and comparison, rejects invalid or unsupported source contracts and canonical baselines, and never executes described CLIs or fetches remote references.
- Offers per-invocation and shared telemetry opt-outs; best-effort activation telemetry sends no schema content, paths, command names, defaults, or diagnostics.
