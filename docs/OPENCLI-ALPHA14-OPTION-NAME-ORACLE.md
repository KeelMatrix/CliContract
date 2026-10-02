# OpenCLI alpha.14 option-name oracle

This document records the exact upstream source record for CliContract's OpenCLI `1.0.0-alpha.14` option-name boundary. It is the canonical source for why a source option name or alias beginning with `-` is rejected with `OPENCLI_OPTION_NAME` instead of being normalized by removing leading dashes.

## OpenCLI generator

The pinned OpenCLI generator passes each source flag name literally into its Cobra flag-entry value for both global and command-local flags.

- **Repository:** [`bcdxn/opencli`](https://github.com/bcdxn/opencli)
- **Tag:** `spec/v1.0.0-alpha.14`
- **File:** `gen/cli_cobra.go`
- **Lines:** 155–163
- **Tagged source:** [`gen/cli_cobra.go#L155-L163`](https://github.com/bcdxn/opencli/blob/spec/v1.0.0-alpha.14/gen/cli_cobra.go#L155-L163)

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

The same literal assignment is used for command-local flags at lines 363–371: [`gen/cli_cobra.go#L363-L371`](https://github.com/bcdxn/opencli/blob/spec/v1.0.0-alpha.14/gen/cli_cobra.go#L363-L371).

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

The generated Cobra runner binds that literal name as the flag name:

- **File:** `gen/templates/code/cobra/gencli/run.tmpl`
- **Lines:** 27–30
- **Tagged source:** [`run.tmpl#L27-L30`](https://github.com/bcdxn/opencli/blob/spec/v1.0.0-alpha.14/gen/templates/code/cobra/gencli/run.tmpl#L27-L30)

```gotemplate
{{- range .GlobalFlags}}
	// Global Flags
	rootCmd.PersistentFlags().{{.CobraBindFn}}(&{{.VarName}}, {{goString .FlagName}}, {{goString .Shorthand}}, {{.Default}}, {{goString .Summary}})
{{- end}}
```

The template supplies the literal `FlagName` to Cobra's pflag-backed binder; the corresponding long-form invocation is rendered and parsed as `--` followed by that name.

## pflag parser

The pinned pflag parser removes the two long-form prefix characters, then rejects an empty name, a name beginning with `-`, or a name beginning with `=`.

- **Repository:** [`spf13/pflag`](https://github.com/spf13/pflag)
- **Version:** `v1.0.10`
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

## Invocation-semantics consequence

Together, these pinned points establish that alpha.14 carries the source spelling literally into a parser whose long form is `--<name>`. A source spelling beginning with a dash is therefore not representable unambiguously: stripping the dash would change invocation identity, while retaining it produces an invalid long-form name. Such primary names and aliases must fail closed with `OPENCLI_OPTION_NAME`.

This record anchors the option-name rule; runtime behavior and regression coverage remain in the normalizer, compatibility rules, tests, and packed-tool checks.
