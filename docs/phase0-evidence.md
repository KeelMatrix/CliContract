# Phase 0 evidence

This document records the probe boundary. It is not a product README and does not define a shipped command surface.

## Pinned standards

- OpenCLI specification: `1.0.0-alpha.14`, primary specification page `https://opencli.dev/specification`, repository `https://github.com/bcdxn/opencli`, main revision `683d0ca92fc37ccc2626e64db0a8c32f3c4063c0`, checked 2026-09-21.
- The repository README identifies `ocli` as the current OpenCLI validator/documentation/code-generation tool and shows alpha.14 examples. The latest release check returned `v1.3.0` on 2026-09-21. No first-party compatibility-diff command was found in the listed tool capabilities; compatibility detection is an intended use of the format, not a shipped diff product. `where.exe ocli` returned no local installation.
- Adjacent current CLI compatibility product checked on 2026-09-21: jdx `usage diff` (`https://usage.jdx.dev/cli/diff`) compares two CLI specs and classifies changes as breaking, compatible, or metadata-only. It is relevant adjacent competition, not an OpenCLI first-party tool and not a name collision.
- .NET CLI-schema: .NET 10.0.401 SDK CLI introspection output was captured locally. Microsoft’s .NET 10 announcement (`https://devblogs.microsoft.com/dotnet/announcing-dotnet-10/`, accessed 2026-09-21) documents `--cli-schema` as machine-readable command description output. The Microsoft .NET 10 SDK notes (`https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10`, accessed 2026-09-21) document the command shape. Pinned SDK 8.0.425 was checked locally and rejected the flag.
- System.CommandLine: current Microsoft syntax documentation (`https://learn.microsoft.com/en-us/dotnet/standard/commandline/syntax`, accessed 2026-09-21) describes commands, options, arguments, aliases, defaults, arity, and choices. The upstream OpenCLI support discussion (`https://github.com/dotnet/command-line-api/issues/2632`, accessed 2026-09-21) records no unified exported contract for general application CLIs.

## Fixture policy

`fixtures/opencli/phase0.json` is an authored JSON corpus covering nested commands, aliases, positional arguments, required/optional values, arity, choices, defaults, Unicode text, empty subcommands, and source-order variation. `fixtures/opencli/phase0-regressions.json` additionally covers root global flags, scalar choice values, declaration-ordered positional arguments, alternative default sources, and omitted versus explicit defaulted fields. The positional argument arrays intentionally retain their declared order; only source ordering that is not semantically meaningful is varied in the reordered fixture.

The pinned upstream alpha.14 examples are included as `fixtures/opencli/petstore-cli.ocs.json` and `fixtures/opencli/globalflags-cli.ocs.yaml`. The upstream repository revision `683d0ca92fc37ccc2626e64db0a8c32f3c4063c0` is MIT licensed (`Copyright (c) 2026 bcdxn`), so these fixtures are included with source and license attribution here.

`fixtures/dotnet/*.json` includes raw SDK output captures and a small synthetic-minimal fixture preserving the observed shape for deterministic unit coverage. The synthetic fixture is not evidence of a general application contract.

## Per-attribute survival table

| attribute | OpenCLI source field | .NET CLI-schema source field | canonical representation | round-trip evidence | survives without inference? |
|---|---|---|---|---|---|
| command removed | command map key | `subcommands` map key | command logical path | `phase0` canonical command paths; SDK captures contain nested keys | OpenCLI yes; .NET yes for names |
| option removed | `flags[].name` | `options` map key | `CanonicalOption.Name` | deploy/region fixtures | OpenCLI yes; .NET yes |
| argument removed | `args[].name` | `arguments` map key | `CanonicalArgument.Name` | deploy fixture and SDK captures | OpenCLI yes; .NET yes |
| callable alias removed | `commands[].aliases` | observed `subcommands[].aliases` | `CanonicalCommand.Aliases` | deploy alias `ship`; SDK `execute` alias `exec` | OpenCLI yes; .NET not advertised |
| optional -> required | `required` | option `required`; argument `arity.minimum` | `Required` plus arity | deploy region and synthetic format | OpenCLI yes; .NET options yes, arguments are contract-derived from arity |
| accepted arity narrowed | `variadic`, `minItems`, `maxItems`, `required` | `arity.minimum`, `arity.maximum` | `ArityMinimum`, `ArityMaximum` | tag and synthetic format | yes for represented arity |
| required type/domain narrowed | `type`, `choices` | `valueType`; no structured choices | `Type`, scalar `AllowedValues` | region choices and SDK `valueType` | OpenCLI yes; .NET type yes, domain no |
| command added | command map key | `subcommands` map key | command logical path | nested fixture/captures | yes for names |
| optional option added | flag item with `required:false` | option `required:false` | option name + required | region fixture and SDK options | OpenCLI yes; .NET yes |
| alias added | `aliases` | `aliases` on options only | sorted alias arrays | region `-r`, SDK `-f` | OpenCLI yes; .NET option aliases yes, callable aliases no |
| default-value change | `default` | `hasDefaultValue`, `defaultValue` | `DefaultValue` plus ordered `AlternativeSources` | region/default-source fixtures and SDK defaults | yes when present |
| description/help change | `summary`, `description` | `description` | summary/description | Unicode and SDK descriptions | yes |
| deprecation/status change | not represented by alpha.14 fields | not represented | `Status` remains null | raw schemas contain no deprecation/status field | no; not advertised as a represented rule |

## OpenCLI alpha.14 contract boundary

The Phase 0 canonical manifest is an explicit compatibility contract, not a lossless copy of every OpenCLI field. The
adapter parses the complete bounded document, validates fields that are part of the supported shape, and ignores
outside fields as non-contract data. Outside fields do not affect canonical bytes or compatibility decisions. Unknown
fields and `x-*` extension fields follow the same ignore policy. Resource limits still apply while parsing ignored data.

### Inside the v1 compatibility contract

- `opencliVersion` is required to be exactly `1.0.0-alpha.14` and is retained as the canonical `SourceVersion`.
- `info.title`, `info.binary`, and `info.version` are required strings for input validation. They identify and validate
  the source document but are not compatibility attributes and are not retained in the canonical manifest.
- The `commands` map keys become logical command paths. Command `aliases`, `summary`, and `description` are retained;
  root-command aliases are retained on `Root` exactly like aliases on nested commands.
- `global.flags` is represented as root options. Command `flags` and `args` are represented; argument declaration order
  is retained, while option and alias collections are canonically sorted.
- Parameter `name`, `type`, `required`, `variadic`, `minItems`, and `maxItems` are represented as canonical name, type,
  required state, and minimum/maximum arity.
- Parameter `summary` and `description` are retained. Flag aliases are retained and sorted.
- `choices[].value` is retained only when it is a scalar string, number, or boolean. Choice declaration order is
  canonically sorted by scalar value; choice descriptions are outside the contract.
- Scalar `default` values are retained. Ordered `alternativeSources` entries retain their `$ENV`/`$FILE` type and
  property in source order.

### Outside the v1 compatibility contract

The adapter explicitly ignores these alpha.14 fields rather than treating them as compatibility semantics:

- top-level `install`;
- `info.summary`, `info.description`, `info.license` (`name`, `spdxId`, `url`), and `info.contact` (`name`, `email`,
  `url`);
- `global.exitCodes` and `global.config` (`json`, `toml`, and `yaml` paths);
- command `hidden`, `kind`, `exitCodes`, and `examples`;
- argument `passthrough`;
- flag `hint` and `hidden`;
- choice `description` fields;
- every `x-*` extension field and any other upstream field not represented above.

Invalid values for recognized in-contract fields are rejected as bounded adapter errors. A non-scalar `default`, an
explicit null container, or another malformed recognized value is not outside data and is not silently ignored.

## Verdict

**PARTIAL PASS.** OpenCLI alpha.14 passes the represented v1 compatibility attributes and deterministic normalization. The .NET CLI-schema probe is not advertised because the observed SDK output has no explicit format version, no structured allowed-value/domain field, and the flag is documented and observed as .NET SDK CLI introspection rather than a stable general application export. The raw capture also contains a callable alias at `fixtures/dotnet/tool-cli-schema.json:7-12` (`subcommands.execute.aliases: ["exec"]`), so callable-alias absence is not a valid reason for the decision. The proposed v1 `--input` kinds are `auto|opencli`; `dotnet` is not advertised.

## Determinism and safety

The exact commands and output hashes are appended before push. The test suite compares the original and reordered/CRLF OpenCLI fixtures, while preserving positional argument declaration order as contract data. Adapter failures are bounded as documented `NormalizationException` errors and do not escape as raw process failures. The no-execution check is `pwsh ./scripts/check-no-execution.ps1`; it scans every tracked source/config file with a source extension across the repository. `pwsh ./scripts/test-no-execution.ps1` proves an explicit forbidden-reference fixture is rejected. These checks prove only the files scanned by the command and do not prove host-level behavior or untracked/future files outside that set.

The probe reports normalization failures as `CODE: message` with exit code `3`; unexpected adapter failures are mapped to the documented `NORMALIZATION_ERROR` code. Probe infrastructure failures use `INTERNAL_ERROR` and exit code `4`, while missing input remains an invocation error with exit code `2`.

## Raw freshness and collision checks

Access date: 2026-09-21. Commands were run immediately before repository creation/push preparation.

~~~
curl.exe -sS -i https://api.nuget.org/v3-flatcontainer/keelmatrix.clicontract/index.json
HTTP/1.1 404 Not Found

curl.exe -sS -i https://api.nuget.org/v3/registration5-gz-semver2/keelmatrix.clicontract/index.json
HTTP/1.1 404 Not Found

gh repo view KeelMatrix/CliContract
GraphQL: Could not resolve to a Repository with the name 'KeelMatrix/CliContract'. (repository)
EXIT=1

Get-Command clicontract; where.exe clicontract
INFO: Could not find files for the given pattern(s).
WHERE_EXIT=1

dotnet tool search clicontract --take 20
Could not find any results.

gh search repos CliContract --limit 20 --json fullName,isPrivate,url
[]

curl.exe -sS -i https://api.nuget.org/v3-flatcontainer/clicontract/index.json
HTTP/1.1 404 Not Found
~~~

Measured durations were 858 ms, 372 ms, 668 ms, 368 ms, 1451 ms, 799 ms, and 387 ms respectively. The GitHub search command was corrected after an invalid field-name probe; the recorded collision result is the corrected fullName,isPrivate,url invocation above.

The OpenCLI revision command was:

~~~
curl.exe -sS https://api.github.com/repos/bcdxn/opencli/commits/main
"sha": "683d0ca92fc37ccc2626e64db0a8c32f3c4063c0"
EXIT=0
DURATION_MS=1559
~~~

## Raw .NET capture commands

The first four commands ran from the temporary directory under SDK 10.0.401; the output files are kept byte-for-byte in fixtures/dotnet.

~~~
dotnet --cli-schema                 EXIT=0 DURATION_MS=827  -> root-cli-schema.json
dotnet build --cli-schema           EXIT=0 DURATION_MS=318  -> build-cli-schema.json
dotnet add package --cli-schema     EXIT=0 DURATION_MS=363  -> add-package-cli-schema.json
dotnet tool --cli-schema             EXIT=0 DURATION_MS=344  -> tool-cli-schema.json
dotnet --version                    EXIT=0 DURATION_MS=216  -> sdk-10.0.401-version.txt
~~~

The pinned SDK 8.0.425 commands ran from the repository root:

~~~
dotnet --version
8.0.425
EXIT=0
DURATION_MS=373

dotnet --cli-schema
Possible reasons for this include:
  * You misspelled a built-in dotnet command.
  * You intended to execute a .NET program, but dotnet---cli-schema does not exist.
  * You intended to run a global tool, but a dotnet-prefixed executable with this name could not be found on the PATH.
Could not execute because the specified command or file was not found.
EXIT=1
DURATION_MS=433
~~~

SHA-256 hashes of the kept raw captures:

~~~
0F6CC553F80F258C9522923A1379DB4788E5FA420711632FDA3256F8C55E3140  fixtures/dotnet/add-package-cli-schema.json
5897C158D0270B66C9C2DC4AD1B6A652554EE79DDFE71FD1381C4C3EE8BCE825  fixtures/dotnet/build-cli-schema.json
513C2A9C47D02876E5BD550C93A70332AC9BB0D4E7E17FE7FEBC20BCFBDC2AC8  fixtures/dotnet/root-cli-schema.json
3DB6BCA4B50618B2E0D2D55D23B3D0541FE039AB6F731DF0E9C47317B400CE52  fixtures/dotnet/sdk-10.0.401-version.txt
2731BE9606B8A6BD33FF29FC4BA6DE9C199367C96BC27056024A3399D8377A82  fixtures/dotnet/sdk-8.0.425-cli-schema.txt
EDD0AD505C4A86B69A22139EAD7657FF889802887824385E6334D544555DBBB7  fixtures/dotnet/sdk-8.0.425-version.txt
BAC58B3B461C0C2D0000F0CD10E9306E11EBD58E503AAD80853A8888EF3FCACC  fixtures/dotnet/tool-cli-schema.json
~~~

## Determinism proof

Exact command:

~~~
pwsh -NoProfile -File ./scripts/verify-determinism.ps1
~~~

Output: four normalizations succeeded and PASS: repeated, semantically reordered, and CRLF inputs produced identical canonical bytes. The canonical output hash was 9CEDDD29443CFB9D995B6412AF3A42031E2B2CC6369D1543D1A6B649DC8AFD05 for the original, repeated, reordered, and CRLF-output cases; the CRLF input itself had the distinct hash 7D0127E74605CC35AE5D9F734C426DDB248F8D6AD714B0C2416BFC75794A9A7B. The final combined determinism/no-execution/fixture-check command completed in 11.2 seconds on Windows. No Linux container or second OS was available, so cross-OS byte identity remains unverified.

## No-execution/no-network proof

Exact command and output:

~~~
pwsh -NoProfile -File ./scripts/check-no-execution.ps1
PASS: scanned 9 tracked source/config file(s) plus 0 explicit file(s); no process-start, network, dynamic-load, or activation reference found. This proves only the scanned files, not runtime behavior or untracked/future files.
EXIT=0
~~~

The check now enumerates every tracked source/config file with a recognized source extension across the repository, including probe, tests, future source directories, project configuration, and solution files. It does not prove runtime behavior, dependencies, or untracked/future files outside the scanned set. The fixture regression command is `pwsh -NoProfile -File ./scripts/test-no-execution.ps1`; it fails closed when `fixtures/no-execution/forbidden-reference.txt` is passed as an explicit scan input.
