$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$oraclePath = Join-Path $root 'docs/OPENCLI-ALPHA14-OPTION-NAME-ORACLE.md'
if (-not (Test-Path -LiteralPath $oraclePath -PathType Leaf)) {
    throw "The pinned alpha.14 option-name oracle is missing: $oraclePath"
}

$oracle = Get-Content -LiteralPath $oraclePath -Raw
$requiredOracleText = @(
    'https://github.com/bcdxn/opencli/blob/spec/v1.0.0-alpha.14/gen/cli_cobra.go#L155-L163',
    'https://github.com/bcdxn/opencli/blob/spec/v1.0.0-alpha.14/gen/cli_cobra.go#L363-L371',
    'https://github.com/bcdxn/opencli/blob/spec/v1.0.0-alpha.14/gen/templates/code/cobra/gencli/run.tmpl#L27-L30',
    'https://github.com/spf13/pflag/blob/v1.0.10/flag.go#L980-L986',
    'FlagName:     flag.Name,',
    'rootCmd.PersistentFlags().{{.CobraBindFn}}(&{{.VarName}}, {{goString .FlagName}}, {{goString .Shorthand}}, {{.Default}}, {{goString .Summary}})',
    'name := s[2:]',
    "if len(name) == 0 || name[0] == '-' || name[0] == '='",
    'not representable unambiguously',
    'must fail closed'
)

foreach ($required in $requiredOracleText) {
    if (-not $oracle.Contains($required)) {
        throw "The pinned alpha.14 option-name oracle is missing required citation: $required"
    }
}

$flagNameAssignments = [regex]::Matches($oracle, 'FlagName:\s+flag\.Name,').Count
if ($flagNameAssignments -ne 2) {
    throw "Expected exactly two literal generator FlagName assignments, found $flagNameAssignments."
}

$linkedSurfaces = @(
    'README.md',
    'COMPATIBILITY-RULES.md',
    'MANIFEST.md',
    'SECURITY.md',
    'CHANGELOG.md',
    'docs/OPENCLI-ALPHA14-CONFORMANCE.md',
    'docs/ERROR-TAXONOMY.md',
    'src/KeelMatrix.CliContract.Tool/README.md',
    'src/KeelMatrix.CliContract.Tool/Program.cs'
)

foreach ($relativePath in $linkedSurfaces) {
    $path = Join-Path $root $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "The option-name oracle link surface is missing: $relativePath"
    }

    $content = Get-Content -LiteralPath $path -Raw
    if (-not $content.Contains('OPENCLI-ALPHA14-OPTION-NAME-ORACLE')) {
        throw "The option-name oracle is not linked from $relativePath"
    }
}

Write-Output 'OPTION_NAME_ORACLE_GUARD=PASS'
