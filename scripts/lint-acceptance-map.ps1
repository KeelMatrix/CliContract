param(
    [Parameter(Mandatory = $true)]
    [string] $ChecklistPath,

    [Parameter(Mandatory = $true, ParameterSetName = 'Text')]
    [string] $MapText,

    [Parameter(Mandatory = $true, ParameterSetName = 'Path')]
    [string] $MapPath,

    [Parameter(Mandatory = $true, ParameterSetName = 'Stdin')]
    [switch] $MapFromStdin,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string] $CandidateSha,

    [string] $RepositoryRoot = (Get-Location).Path,

    [string] $Repository = 'KeelMatrix/CliContract'
)

$ErrorActionPreference = 'Stop'

if ($MyInvocation.InvocationName -ne '.') {
    $module = Import-Module (Join-Path $PSScriptRoot 'private\acceptance-map.psm1') -Force -PassThru
    & "$($module.Name)\Invoke-AcceptanceMapLint" @PSBoundParameters
}
