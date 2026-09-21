# .NET CLI-schema captures

The JSON files in this directory are raw output from the installed .NET 10.0.401 SDK. Each command was run from a temporary directory so no project or target application was loaded.

| file | producing command | producing SDK |
|---|---|---|
| \`root-cli-schema.json\` | \`dotnet --cli-schema\` | \`10.0.401\` |
| \`build-cli-schema.json\` | \`dotnet build --cli-schema\` | \`10.0.401\` |
| \`add-package-cli-schema.json\` | \`dotnet add package --cli-schema\` | \`10.0.401\` |
| \`tool-cli-schema.json\` | \`dotnet tool --cli-schema\` | \`10.0.401\` |

The SDK 8.0.425 check was run from this repository, which is pinned by \`global.json\`. It does not support the flag; \`sdk-8.0.425-cli-schema.txt\` contains the raw command-not-found output and \`sdk-8.0.425-version.txt\` contains \`8.0.425\`.

The raw SDK output is evidence of the observed shape only. It does not establish a stable, general application-CLI contract.
