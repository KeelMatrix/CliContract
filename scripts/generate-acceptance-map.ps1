param(
    [Parameter(Mandatory = $true)]
    [string] $ChecklistPath,

    [Parameter(Mandatory = $true)]
    [string] $EvidencePath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string] $CandidateSha,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath,

    [string] $Repository = 'KeelMatrix/CliContract'
)

$ErrorActionPreference = 'Stop'

if ($MyInvocation.InvocationName -ne '.') {
    $module = Import-Module (Join-Path $PSScriptRoot 'private\acceptance-map.psm1') -Force -PassThru
    & "$($module.Name)\Invoke-AcceptanceMapGeneration" @PSBoundParameters
}
