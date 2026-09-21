# Changelog

This file records user-visible changes to KeelMatrix CliContract.

## [Unreleased]

### Added

- OpenCLI `1.0.0-alpha.14` snapshot, validation, diff, and baseline-check commands.
- Deterministic versioned canonical manifests with bounded offline parsing.
- Stable compatibility diagnostics, JSON/text output, explicit suppressions, and CI exit codes.

### Fixed

- Fail closed on invalid recognized OpenCLI fields, duplicate keys, unsupported references, and malformed UTF-8.
- Canonicalize equivalent numeric values and separate requiredness changes from implicit arity changes.
- Reject contradictory command-line options and use the shared bounded telemetry activation contract.
