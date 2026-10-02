# Changelog

This file records user-visible changes to KeelMatrix.CliContract.

## [Unreleased]

### Fixed

- Rejected option source names and aliases beginning with `-` with `OPENCLI_OPTION_NAME`, preserving invocation-relevant identity instead of collapsing spellings through leading-dash trimming; the exact upstream source record is in [`docs/OPENCLI-ALPHA14-OPTION-NAME-ORACLE.md`](docs/OPENCLI-ALPHA14-OPTION-NAME-ORACLE.md).
- Bounded aggregate derived-invocation characters before Cartesian path allocation across source normalization, canonical admission, and compatibility comparison.
- Corrected the pinned alpha.14 command-key grammar to exclude CR from its delimiter class and preserve regex whitespace-edge segmentation. Keys whose pinned derivation contains an empty edge segment are rejected before canonical path parsing with a documented `OPENCLI_COMMAND_KEY` supported-input diagnostic because canonical schema version 2 cannot represent an empty root or command segment.
- Canonical baseline validation rejects states the pinned alpha.14 normalizer cannot produce, including invalid metadata, arity, source, domain, status, file-source, and exit-code invariants; `check` and `diff` return bounded exit `3` for invalid baselines.
- Telemetry activation now follows the canonical runnable-action surface; aliases, parameters, help metadata, and group-only command trees do not activate telemetry.
- Repository wording guards now cover attribution keywords split at internal character boundaries and Markdown hard-wrap whitespace, while packed text inspection strictly decodes and scans both `.nupkg` and `.snupkg` entries.
- Package validation now fails closed outside the canonical repository-root context selected by `global.json`, and reproducibility checks compare both package archives byte-for-byte.

### Added

- Explicit input selection now rejects canonical operands, while `diff --input auto` documents and accepts source, canonical, and mixed operand pairs.
- Canonical manifest schema version `2` preserves global options separately from root-local options, materializes derived command groups, and compares inherited option surfaces at every command.
- Canonical baseline reading now enforces the complete source-producible alpha.14 invariant—including requiredness-derived non-variadic arity, the variadic required/min/max matrix, scalar choice order, and non-empty global file-source configuration—and returns bounded exit `3` for malformed-but-valid baseline states.
- OpenCLI `1.0.0-alpha.14` snapshot, validation, diff, and baseline-check commands.
- Deterministic versioned canonical manifests with bounded offline parsing and exact cross-JSON/YAML finite-number canonicalization, including trailing-dot exponent mantissas.
- Stable compatibility diagnostics, JSON/text output, explicit suppressions, and CI exit codes.
- Informational `KMCLI005` findings for represented metadata, installation guidance, summary/description text, and choice descriptions, while preserving non-gating behavior for both failure thresholds.
- Compatibility coverage for accepted command/option invocation graphs, retained aliases, all supported type-pair domains, constrained choices, binary identity, runnable command kind, positional slots, default-source resolution, global file-source configuration, and global/command exit-code warnings.
- Pinned alpha.14 conformance corpus with variadic cross-field validation, deterministic JSON/YAML equivalence checks, and role-aware input errors.
- Fail-closed packed-tool and symbol-package archive validation.
- Alpha.14 command-key parsing now follows the pinned ASCII delimiter and tagged modifier grammar, and tagged logical validation covers groups, positional ordering, variadic flags, duplicate accepted names, `$FILE` prerequisites, and typed defaults.
- Canonical manifests enforce accepted-invocation uniqueness after both source normalization and manifest parsing; exact numeric domains no longer use a decimal-range boundary.
- Typed constrained choices and defaults now fail closed when they are not representable by their declared type, and exact numeric domain logic is shared across validation, canonicalization, and compatibility comparison to preserve self-reflexivity.
- Public error documentation now distinguishes missing source/baseline paths (exit `2`) from present invalid or unreadable source/baseline files (exit `3`), with a shared taxonomy table.
