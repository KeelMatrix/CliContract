$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$oraclePath = Join-Path $root 'docs/OPENCLI-ALPHA14-OPTION-NAME-ORACLE.md'
if (-not (Test-Path -LiteralPath $oraclePath -PathType Leaf)) {
    throw "The pinned alpha.14 option-name oracle is missing: $oraclePath"
}

$oracle = [IO.File]::ReadAllText($oraclePath).Replace("`r`n", "`n")
$requiredOracleText = @(
    'https://github.com/bcdxn/opencli/blob/spec/v1.0.0-alpha.14/gen/cli_shared.go#L119-L131',
    'https://github.com/bcdxn/opencli/blob/spec/v1.0.0-alpha.14/gen/cli_cobra.go#L135',
    'https://github.com/bcdxn/opencli/blob/spec/v1.0.0-alpha.14/gen/cli_cobra.go#L361',
    'https://github.com/bcdxn/opencli/blob/spec/v1.0.0-alpha.14/gen/templates/code/cobra/gencli/run.tmpl#L27-L43',
    'https://github.com/bcdxn/opencli/blob/spec/v1.0.0-alpha.14/gen/cli_cobra.go#L268-L273',
    'https://github.com/bcdxn/opencli/blob/spec/v1.0.0-alpha.14/gen/templates/code/cobra/gencli/command.tmpl#L18-L24',
    'https://github.com/bcdxn/opencli/blob/spec/v1.0.0-alpha.14/gen/cli_cobra.go#L155-L163',
    'https://github.com/bcdxn/opencli/blob/spec/v1.0.0-alpha.14/gen/cli_cobra.go#L363-L371',
    'https://github.com/spf13/pflag/blob/v1.0.10/flag.go#L980-L986',
    '**Commit:** `683d0ca92fc37ccc2626e64db0a8c32f3c4063c0`',
    '**Commit:** `0491e5702ad2bb108bc519a5221bcc0f52aa9564`',
    'first alias whose Go byte length is one as the shorthand',
    'Two or more single-byte aliases cannot be sorted',
    'Command aliases have no shorthand role',
    'Reordering it preserves the accepted command-name set and cannot change a flag shorthand role, so canonical sorting is semantically neutral for command invocation.',
    'FlagName:     flag.Name,',
    'OPENCLI_OPTION_NAME` and exit code `3`'
)

foreach ($required in $requiredOracleText) {
    if (-not $oracle.Contains($required)) {
        throw "The pinned alpha.14 option-name oracle is missing required citation: $required"
    }
}

$splitAliasesBlock = @'
// splitAliases returns the first single-char alias as shorthand and all other aliases.
func splitAliases(aliases []string) (string, []string) {
	shorthand := ""
	extraAliases := make([]string, 0, len(aliases))
	for _, a := range aliases {
		if len(a) == 1 && shorthand == "" {
			shorthand = a
			continue
		}
		extraAliases = append(extraAliases, a)
	}
	return shorthand, extraAliases
}
'@
$runnerAliasesBlock = @'
{{- range .GlobalFlags}}
	// Global Flags
	rootCmd.PersistentFlags().{{.CobraBindFn}}(&{{.VarName}}, {{goString .FlagName}}, {{goString .Shorthand}}, {{.Default}}, {{goString .Summary}})
{{- end}}
{{- if hasExtraAliases .GlobalFlags}}
	rootCmd.PersistentFlags().SetNormalizeFunc(func(_ *pflag.FlagSet, name string) pflag.NormalizedName {
		switch name {
{{- range .GlobalFlags}}
{{- $flagName := .FlagName}}
{{- range .ExtraAliases}}
		case {{goString .}}:
			return pflag.NormalizedName({{goString $flagName}})
{{- end}}
{{- end}}
		}
		return pflag.NormalizedName(name)
	})
'@
$commandAliasSourceBlock = @'
	cmdCore := buildCommandFileCore(
		cmd.Segment,
		cmd.Summary,
		cmd.Description,
		cmd.Aliases,
'@
$commandAliasTemplateBlock = @'
	command := &cobra.Command{
		Use:   {{goString .Segment}},
		Short: {{goString .Summary}},
		Long:  {{goString .Description}},
{{- if .Aliases}}
		Aliases: []string{ {{range $i, $a := .Aliases}}{{if $i}}, {{end}}{{goString $a}}{{end}} },
{{- end}}
'@
$pflagLongNameBlock = @'
func (f *FlagSet) parseLongArg(s string, args []string, fn parseFunc) (a []string, err error) {
	a = args
	name := s[2:]
	if len(name) == 0 || name[0] == '-' || name[0] == '=' {
		err = f.fail(&InvalidSyntaxError{specifiedFlag: s})
		return
	}
'@
$requiredVerbatimBlocks = @(
    $splitAliasesBlock,
    $runnerAliasesBlock,
    $commandAliasSourceBlock,
    $commandAliasTemplateBlock,
    $pflagLongNameBlock
)

foreach ($block in $requiredVerbatimBlocks) {
    if (-not $oracle.Contains($block)) {
        throw 'The pinned alpha.14 option-name oracle is missing or has altered verbatim source lines.'
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
