# OpenCLI alpha.14 option-name oracle

This document records the pinned generator and parser behavior that defines CliContract's option-name and alias-role boundaries.

## OpenCLI generator

The OpenCLI source name is passed literally to the Cobra flag binding for global and command-local flags.

- **Repository:** [`bcdxn/opencli`](https://github.com/bcdxn/opencli)
- **Tag:** `spec/v1.0.0-alpha.14`
- **Commit:** `683d0ca92fc37ccc2626e64db0a8c32f3c4063c0`
- **File:** `gen/cli_cobra.go`
- **Lines:** 155–163 (global flags), 363–371 (command-local flags)
- **Tagged source:** [`gen/cli_cobra.go#L155-L163`](https://github.com/bcdxn/opencli/blob/spec/v1.0.0-alpha.14/gen/cli_cobra.go#L155-L163), [`gen/cli_cobra.go#L363-L371`](https://github.com/bcdxn/opencli/blob/spec/v1.0.0-alpha.14/gen/cli_cobra.go#L363-L371)

```go
			globalFlags = append(globalFlags, cobraFlagEntry{
				FieldName:    toPascalCase(flag.Name),
				VarName:      "flag" + toPascalCase(flag.Name),
				FlagName:     flag.Name,
				GoType:       toGoType(flag.Type, flag.Variadic),
				CobraBindFn:  cobraBindFn(flag.Type, flag.Variadic),
				Default:      cobraDefaultVal(flag.Default, flag.Type, flag.Variadic),
				Summary:      flag.Summary,
```

```go
		cobraFlags = append(cobraFlags, cobraFlagEntry{
			FieldName:    toPascalCase(flag.Name),
			VarName:      "flag" + toPascalCase(flag.Name),
			FlagName:     flag.Name,
			GoType:       toGoType(flag.Type, flag.Variadic),
			CobraBindFn:  cobraBindFn(flag.Type, flag.Variadic),
			Default:      cobraDefaultVal(flag.Default, flag.Type, flag.Variadic),
			Summary:      flag.Summary,
```

The generator uses `splitAliases` for both scopes:

- **Repository:** [`bcdxn/opencli`](https://github.com/bcdxn/opencli)
- **Tag:** `spec/v1.0.0-alpha.14`
- **Commit:** `683d0ca92fc37ccc2626e64db0a8c32f3c4063c0`
- **File:** `gen/cli_cobra.go`
- **Lines:** 135 and 361
- **Tagged source:** [`gen/cli_cobra.go#L135`](https://github.com/bcdxn/opencli/blob/spec/v1.0.0-alpha.14/gen/cli_cobra.go#L135), [`gen/cli_cobra.go#L361`](https://github.com/bcdxn/opencli/blob/spec/v1.0.0-alpha.14/gen/cli_cobra.go#L361)

```go
			shorthand, extraAliases := splitAliases(flag.Aliases)
```

```go
		shorthand, extraAliases := splitAliases(flag.Aliases)
```

`splitAliases` selects the first alias whose Go byte length is one as the shorthand and leaves every other alias in the extra long-form list:

- **Repository:** [`bcdxn/opencli`](https://github.com/bcdxn/opencli)
- **Tag:** `spec/v1.0.0-alpha.14`
- **Commit:** `683d0ca92fc37ccc2626e64db0a8c32f3c4063c0`
- **File:** `gen/cli_shared.go`
- **Lines:** 119–131
- **Tagged source:** [`cli_shared.go#L119-L131`](https://github.com/bcdxn/opencli/blob/spec/v1.0.0-alpha.14/gen/cli_shared.go#L119-L131)

```go
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
```

The generated runner binds the literal flag name and selected shorthand, then normalizes extra aliases to the primary name:

- **Repository:** [`bcdxn/opencli`](https://github.com/bcdxn/opencli)
- **Tag:** `spec/v1.0.0-alpha.14`
- **Commit:** `683d0ca92fc37ccc2626e64db0a8c32f3c4063c0`
- **File:** `gen/templates/code/cobra/gencli/run.tmpl`
- **Lines:** 27–43
- **Tagged source:** [`run.tmpl#L27-L43`](https://github.com/bcdxn/opencli/blob/spec/v1.0.0-alpha.14/gen/templates/code/cobra/gencli/run.tmpl#L27-L43)

```gotemplate
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
```

Command aliases have no shorthand role. The generator passes them as command data, and the template assigns them to Cobra's `Command.Aliases` field:

- **Repository:** [`bcdxn/opencli`](https://github.com/bcdxn/opencli)
- **Tag:** `spec/v1.0.0-alpha.14`
- **Commit:** `683d0ca92fc37ccc2626e64db0a8c32f3c4063c0`
- **Files and lines:** `gen/cli_cobra.go` 268–273; `gen/templates/code/cobra/gencli/command.tmpl` 18–24
- **Tagged source:** [`cli_cobra.go#L268-L273`](https://github.com/bcdxn/opencli/blob/spec/v1.0.0-alpha.14/gen/cli_cobra.go#L268-L273), [`command.tmpl#L18-L24`](https://github.com/bcdxn/opencli/blob/spec/v1.0.0-alpha.14/gen/templates/code/cobra/gencli/command.tmpl#L18-L24)

```go
	cmdCore := buildCommandFileCore(
		cmd.Segment,
		cmd.Summary,
		cmd.Description,
		cmd.Aliases,
```

```gotemplate
	command := &cobra.Command{
		Use:   {{goString .Segment}},
		Short: {{goString .Summary}},
		Long:  {{goString .Description}},
{{- if .Aliases}}
		Aliases: []string{ {{range $i, $a := .Aliases}}{{if $i}}, {{end}}{{goString $a}}{{end}} },
{{- end}}
```

The command alias array contributes whole command-name alternatives; it is never passed to `splitAliases` or bound as a flag. Reordering it preserves the accepted command-name set and cannot change a flag shorthand role, so canonical sorting is semantically neutral for command invocation.

## pflag parser

The pinned pflag parser removes the two-character long-form prefix, then rejects an empty name or one beginning with `-` or `=`.

- **Repository:** [`spf13/pflag`](https://github.com/spf13/pflag)
- **Tag:** `v1.0.10`
- **Commit:** `0491e5702ad2bb108bc519a5221bcc0f52aa9564`
- **File:** `flag.go`
- **Lines:** 980–986
- **Tagged source:** [`flag.go#L980-L986`](https://github.com/spf13/pflag/blob/v1.0.10/flag.go#L980-L986)

```go
func (f *FlagSet) parseLongArg(s string, args []string, fn parseFunc) (a []string, err error) {
	a = args
	name := s[2:]
	if len(name) == 0 || name[0] == '-' || name[0] == '=' {
		err = f.fail(&InvalidSyntaxError{specifiedFlag: s})
		return
	}
```

## Representability

The sorted canonical alias list preserves the exact accepted option invocation set when it contains zero or one single-byte alias: that alias's shorthand role is then determined by its value, independent of list order. A multi-byte alias such as `é` is not a shorthand under Go's byte-length test. Two or more single-byte aliases cannot be sorted without possibly changing which alias alpha.14 accepts as `-x`; those source states fail with `OPENCLI_OPTION_NAME`. Comparisons represent a primary name as `--name`, the one shorthand alias as `-x`, and every other alias as `--alias`.

The pflag boundary also means that empty option names or aliases and source spellings beginning with `-` or `=` cannot be represented faithfully. CliContract rejects them with `OPENCLI_OPTION_NAME` and exit code `3` rather than stripping characters or reporting a clean comparison.
