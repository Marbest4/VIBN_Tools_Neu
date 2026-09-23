[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$FeeScreenSimRoot,
    [string]$AdditionalPackageSource,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts\Build-Common.ps1')
$feeRoot = Resolve-FeeScreenSimRoot -ExplicitRoot $FeeScreenSimRoot

Push-Location $PSScriptRoot
try {
    if (-not $NoRestore) {
        if (-not [string]::IsNullOrWhiteSpace($AdditionalPackageSource)) {
            $packageSource = (Resolve-Path -LiteralPath $AdditionalPackageSource).Path
            dotnet restore VIBN_Tools_App.sln "-p:RestoreAdditionalProjectSources=$packageSource"
        }
        else {
            dotnet restore VIBN_Tools_App.sln
        }
        Assert-LastExitCode 'NuGet-Wiederherstellung'
    }

    $buildArguments = @(
        'build', 'VIBN_Tools_App.sln',
        '--configuration', $Configuration,
        '--no-restore',
        "-p:FEE_SCREEN_SIM_ROOT=$feeRoot",
        '--verbosity', 'minimal'
    )
    dotnet @buildArguments
    Assert-LastExitCode 'Solution-Build'
}
finally {
    Pop-Location
}

Write-Host "Build erfolgreich. FEE SDK: $feeRoot"
