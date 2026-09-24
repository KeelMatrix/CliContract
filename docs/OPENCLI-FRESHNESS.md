# OpenCLI compatibility boundary

This note records the standards refresh completed on 2026-09-24 for the supported OpenCLI `1.0.0-alpha.14` input contract.

## Upstream surfaces checked

- The [current OpenCLI specification](https://opencli.dev/specification) advertises `1.0.0-alpha.16` and says that full validation requires the `ocli check` command because JSON Schema cannot express every rule.
- The pinned [alpha.14 schema](https://github.com/bcdxn/opencli/blob/spec/v1.0.0-alpha.14/spec.schema.json) provides the structural reference for this adapter.
- The [OpenCLI release history](https://github.com/bcdxn/opencli/releases) now includes alpha.15, stable 1.0.0, and tooling through 1.3.0. The current tooling documents `ocli check`; no first-party compatibility-diff command was identified.
- The local environment did not contain an `ocli` executable. The repository therefore owns the pinned validity corpus and its normative cross-field oracle rather than downloading or invoking tooling during a build.

## .NET feasibility refresh

The .NET 10 SDK now exposes `--cli-schema`, which emits a .NET-specific command-tree description. The official [.NET SDK documentation](https://github.com/dotnet/docs/blob/main/docs/core/whats-new/dotnet-10/sdk.md) and the [open issue on output consistency](https://github.com/dotnet/sdk/issues/49500) show that this surface is an evolving introspection format, not an OpenCLI alpha.14 compatibility artifact. The [System.CommandLine documentation](https://learn.microsoft.com/en-us/dotnet/standard/commandline/) and its [current release history](https://github.com/dotnet/command-line-api/releases) provide parser, alias, arity, help, and invocation APIs, but do not provide a pinned OpenCLI alpha.14 schema exporter or compatibility-diff contract.

The deliberate shipping boundary remains OpenCLI-only, alpha.14-pinned, offline, and schema-driven. A .NET CLI-schema or System.CommandLine adapter would require a separate versioned mapping and compatibility contract, so neither is accepted or advertised by this release line. No network fetch or external validator is part of core behavior.
