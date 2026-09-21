# Privacy

KeelMatrix CliContract processes command-line schema files locally. Version 0.1 emits no telemetry because this standalone tool has no stable shared telemetry integration. It makes no network requests after restore, does not execute the described CLI, and does not write schema contents to logs or telemetry.

The tool reads only paths supplied to its commands and writes a snapshot only when `snapshot --output` succeeds. Diagnostics use stable codes and logical paths; they do not echo complete schemas, descriptions, defaults, credentials, repository names, or filesystem paths.

`--no-telemetry` is accepted as an explicit opt-out and is honored. No background heartbeat, identifier, repository name, or source content is collected.
