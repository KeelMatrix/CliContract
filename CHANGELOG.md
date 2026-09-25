# Changelog

This file records user-visible changes to KeelMatrix CliContract.

## [Unreleased]

### Fixed

- Canonical baseline validation now rejects every tested alpha.14-unrepresentable state in the source-producibility sweep, including empty contact/install presence, non-letter command segments, exact parameter types, required metadata fields, license/example required fields, and the existing arity, source, domain, status, file-source, and exit-code invariants; `check` and `diff` return bounded exit `3` for each invalid baseline.

### Added
- Canonical manifest schema version `2` preserves global options separately from root-local options, materializes derived command groups, and compares inherited option surfaces at every command.
- Canonical baseline reading now enforces the complete source-producible alpha.14 invariant—including requiredness-derived non-variadic arity, the variadic required/min/max matrix, scalar choice order, and non-empty global file-source configuration—and returns bounded exit `3` for malformed-but-valid baseline states.
- CI history checks now require complete reachable history, with a deterministic earlier-commit regression test; first-release freshness checks record package, repository, product, and first-party diff-surface reviews.
- OpenCLI `1.0.0-alpha.14` snapshot, validation, diff, and baseline-check commands.
- Deterministic versioned canonical manifests with bounded offline parsing and exact cross-JSON/YAML finite-number canonicalization, including trailing-dot exponent mantissas.
- Stable compatibility diagnostics, JSON/text output, explicit suppressions, and CI exit codes.
- Informational `KMCLI005` findings for represented metadata, installation guidance, summary/description text, and choice descriptions, while preserving non-gating behavior for both failure thresholds.
- Compatibility coverage for accepted command/option invocation graphs, retained aliases, all supported type-pair domains, constrained choices, binary identity, runnable command kind, positional slots, default-source resolution, global file-source configuration, and global/command exit-code warnings.
- Pinned alpha.14 conformance corpus with variadic cross-field validation, deterministic JSON/YAML equivalence checks, role-aware input errors, and fail-closed package provenance and sensitive-path gates.
- Fail-closed packed-tool and symbol-package archive validation.
- Alpha.14 command-key parsing now follows the tagged modifier grammar, and tagged logical validation covers groups, positional ordering, variadic flags, duplicate accepted names, `$FILE` prerequisites, and typed defaults.
- Canonical manifests enforce accepted-invocation uniqueness after both source normalization and manifest parsing; exact numeric domains no longer use a decimal-range boundary.
- Typed constrained choices and defaults now fail closed when they are not representable by their declared type, and exact numeric domain logic is shared across validation, canonicalization, and compatibility comparison to preserve self-reflexivity.
- Public error documentation now distinguishes missing source/baseline paths (exit `2`) from present invalid or unreadable source/baseline files (exit `3`), with a shared taxonomy table.
