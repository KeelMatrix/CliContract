. (Join-Path $PSScriptRoot 'acceptance-map-internals.ps1')

function Invoke-AcceptanceMapGeneration {
    param(
        [Parameter(Mandatory = $true)] [string] $ChecklistPath,
        [Parameter(Mandatory = $true)] [string] $EvidencePath,
        [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-fA-F]{40}$')] [string] $CandidateSha,
        [Parameter(Mandatory = $true)] [string] $OutputPath,
        [string] $Repository = 'KeelMatrix/CliContract'
    )
    Invoke-AcceptanceMapGenerationCore @PSBoundParameters
}

function Invoke-AcceptanceMapLint {
    param(
        [Parameter(Mandatory = $true)] [string] $ChecklistPath,
        [Parameter(Mandatory = $true, ParameterSetName = 'Text')] [string] $MapText,
        [Parameter(Mandatory = $true, ParameterSetName = 'Path')] [string] $MapPath,
        [Parameter(Mandatory = $true, ParameterSetName = 'Stdin')] [switch] $MapFromStdin,
        [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-fA-F]{40}$')] [string] $CandidateSha,
        [string] $RepositoryRoot = (Get-Location).Path,
        [string] $Repository = 'KeelMatrix/CliContract'
    )
    Invoke-AcceptanceMapLintCore @PSBoundParameters
}

Export-ModuleMember -Function Invoke-AcceptanceMapGeneration, Invoke-AcceptanceMapLint
