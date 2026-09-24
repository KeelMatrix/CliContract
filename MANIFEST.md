# Canonical Manifest

CliContract writes canonical manifest schema version `1`. The manifest is a KeelMatrix-owned representation and is not an OpenCLI structure dump.

## Top-level shape

```json
{
  "SchemaVersion": 1,
  "Adapter": "opencli",
  "SourceVersion": "1.0.0-alpha.14",
  "Info": { "Title": "Example CLI", "Summary": null, "Description": null, "Binary": "example", "Version": "1.0.0", "License": null, "Contact": null, "Install": [] },
  "GlobalExitCodes": [],
  "GlobalConfig": null,
  "Root": { "Path": "root", "Aliases": [], "Hidden": false, "ExitCodes": [], "Examples": [], "Arguments": [], "Options": [], "Subcommands": [] }
}
```

`Info` preserves the validated metadata from the source, including scalar URL values in `Contact.Url`, `License.Url`, and `Install[].Url`. `Info.Binary` is invocation identity and is compared as breaking when renamed. URLs remain data and are never fetched. `GlobalExitCodes` and command `ExitCodes` preserve code, status, summary, and description; represented changes are warnings. `GlobalConfig.FileSources` contains the supported `json`, `toml`, and `yaml` file-source paths sorted by format; the source object's member order is not represented. Commands contain `Path`, `Kind` (`action` or `group`), sorted `Aliases`, optional `Summary`, `Description`, `Status`, `Hidden`, `ExitCodes`, and `Examples`, plus `Arguments`, `Options`, and `Subcommands`. Parameters contain `Name`, optional `Summary`, `Description`, `Type`, `Required`, `ArityMinimum`, `ArityMaximum`, `Variadic`, `Hint`, `Hidden`, scalar `AllowedValues`, structured `Choices`, scalar `DefaultValue`, ordered `AlternativeSources`, and optional `Status`. Arguments additionally contain `Passthrough`, which defaults to false. Options additionally contain sorted `Aliases`.

The current serializer includes null optional values so null and omission have one stable representation. Unknown manifest fields and versions are rejected. The only accepted `Adapter` is `opencli` with `SourceVersion` `1.0.0-alpha.14`.

## Normalization rules

- UTF-8 encoding with a final LF is used for output; source line endings do not affect bytes.
- Invariant culture is used for scalar conversion.
- Command paths are logical paths beginning at `root`.
- Commands, options, aliases, and scalar choices are ordered with ordinal comparison; positional argument declaration order is retained.
- Accepted command invocation names are modeled as primary names plus aliases at every command segment, so retained aliases preserve descendant invocation paths.
- Non-variadic parameters never carry item bounds. At most one variadic positional argument is accepted, and it must be last; `minItems` and `maxItems` are preserved only for variadic parameters.
- Option names use a `--` prefix in the manifest.
- Omitted OpenCLI booleans use their documented defaults; null/default/alternative-source distinctions are preserved according to the supported adapter contract.
- JSON escaping and indentation are produced by the stable .NET JSON serializer; no machine path or source-document metadata is retained.
- Size, node, depth, string, and collection bounds apply while parsing.
- Numeric scalars use exact invariant canonicalization with plain notation for ordinary magnitudes and scientific notation for extreme magnitudes; JSON numbers and recognized finite YAML integer/float values are reduced by numeric value without floating-point conversion. This includes decimal, exponent, trailing-dot exponent mantissas such as `5.e2`, underscore-separated, hexadecimal, octal, binary, and negative-zero spellings; equivalent values such as `7` and `7.0`, `5.e2` and `500`, `0.00001` and `1e-5`, and negative zero spellings produce identical bytes without floating-point range loss. Explicit YAML string tags remain strings. YAML `.inf` and `.nan` spellings are outside the JSON-number boundary: explicit `!!float` forms are rejected with `OPENCLI_NUMBER`, while untagged forms remain strings.

Semantically equivalent OpenCLI JSON/YAML documents therefore produce byte-identical manifests across Windows, Linux, and macOS when run with the same tool version.

## Version policy

The manifest schema version is independent of the upstream OpenCLI version. A future incompatible manifest or source version must receive an explicit implementation and versioned contract; the current tool fails closed rather than best-effort parsing it. Baselines and current descriptions must use the same manifest and supported source versions.

## Unsupported constructs

The v1 adapter does not interpret runtime behavior, execute commands, scrape help, or fetch remote references. Informational metadata is preserved in `Info` but is not compatibility semantics. See [`COMPATIBILITY-RULES.md`](COMPATIBILITY-RULES.md) for the complete contract boundary.
