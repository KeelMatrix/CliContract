# Canonical Manifest

CliContract writes canonical manifest schema version `1`. The manifest is a KeelMatrix-owned representation and is not an OpenCLI structure dump.

## Top-level shape

```json
{
  "SchemaVersion": 1,
  "Adapter": "opencli",
  "SourceVersion": "1.0.0-alpha.14",
  "Info": { "Title": "Example CLI", "Summary": null, "Description": null, "Binary": "example", "Version": "1.0.0", "License": null, "Contact": null, "Install": [] },
  "Root": { "Path": "root", "Aliases": [], "Arguments": [], "Options": [], "Subcommands": [] }
}
```

`Info` preserves the validated informational metadata from the source, including scalar URL values in `Contact.Url`, `License.Url`, and `Install[].Url`. URLs remain data and are never fetched. Commands contain `Path`, sorted `Aliases`, optional `Summary`, `Description`, and `Status`, plus `Arguments`, `Options`, and `Subcommands`. Parameters contain `Name`, optional `Summary`, `Description`, `Type`, `Required`, `ArityMinimum`, `ArityMaximum`, scalar `AllowedValues`, scalar `DefaultValue`, ordered `AlternativeSources`, and optional `Status`. Options additionally contain sorted `Aliases`.

The current serializer includes null optional values so null and omission have one stable representation. Unknown manifest fields and versions are rejected. The only accepted `Adapter` is `opencli` with `SourceVersion` `1.0.0-alpha.14`.

## Normalization rules

- UTF-8 encoding with a final LF is used for output; source line endings do not affect bytes.
- Invariant culture is used for scalar conversion.
- Command paths are logical paths beginning at `root`.
- Commands, options, aliases, and scalar choices are ordered with ordinal comparison; positional argument declaration order is retained.
- Option names use a `--` prefix in the manifest.
- Omitted OpenCLI booleans use their documented defaults; null/default/alternative-source distinctions are preserved according to the supported adapter contract.
- JSON escaping and indentation are produced by the stable .NET JSON serializer; no machine path or source-document metadata is retained.
- Size, node, depth, string, and collection bounds apply while parsing.
- Numeric scalars use lexical invariant canonicalization with plain notation for ordinary magnitudes and scientific notation for extreme magnitudes; equivalent values such as `7` and `7.0`, `0.00001` and `1e-5`, and negative zero spellings produce identical bytes without floating-point range loss.

Semantically equivalent OpenCLI JSON/YAML documents therefore produce byte-identical manifests across Windows, Linux, and macOS when run with the same tool version.

## Version policy

The manifest schema version is independent of the upstream OpenCLI version. A future incompatible manifest or source version must receive an explicit implementation and versioned contract; the current tool fails closed rather than best-effort parsing it. Baselines and current descriptions must use the same manifest and supported source versions.

## Unsupported constructs

The v1 adapter does not interpret runtime behavior, execute commands, scrape help, or fetch remote references. Informational metadata is preserved in `Info` but is not compatibility semantics. See [`COMPATIBILITY-RULES.md`](COMPATIBILITY-RULES.md) for the complete contract boundary.
