# Security Policy

## Reporting a Vulnerability

Do not disclose security vulnerabilities through a public issue.

Report vulnerabilities privately through a GitHub Security Advisory or by email to `keelmatrix@gmail.com`. Include the
affected version, a concise description, reproduction steps or a minimal test case, the expected and observed impact,
and any relevant environment details. Do not include credentials or other sensitive data in the report.

Security reports are separate from ordinary bug reports and community-conduct reports. We will review a report and
coordinate a responsible fix and disclosure.

## Supported Versions

The maintained `0.1.x` version line is supported with security fixes. Older versions and unreleased development builds
are not guaranteed to receive security updates; use the latest maintained version before reporting whether a problem
remains.

## Input and network safety

Schema parsing and comparison are offline and never fetch a network resource. Remote schema references that would require resolution, including `$ref`, `$dynamicRef`, `$recursiveRef`, includes, and remote document references, fail closed with `OPENCLI_REMOTE_REFERENCE` and exit code `3`. Duplicate normalized argument or option names, including option spellings that normalize to the same long name, fail closed with `OPENCLI_DUPLICATE_PARAMETER` and exit code `3`; command keys that normalize to the same logical path fail closed with `OPENCLI_DUPLICATE_COMMAND_PATH` and exit code `3`. Informational scalar URL values declared by the pinned OpenCLI schema, such as `info.contact.url`, `info.license.url`, and `info.install.url`, are accepted as data, preserved verbatim in the canonical manifest, and never fetched. Optional telemetry is separate from parsing and comparison and can be disabled with `--no-telemetry`, `KEELMATRIX_NO_TELEMETRY=1`, `KEELMATRIX_DEVELOPMENT=true`, or `CI=true`.
