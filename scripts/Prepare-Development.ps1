[CmdletBinding()]
param(
    [string]$FeeScreenSimRoot,
    [string]$AdditionalPackageSource,
    [switch]$DoNotPersistFeeSdk,
    [switch]$SelectFeeSdk
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Build-Common.ps1')
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$feeRoot = if ($SelectFeeSdk -and [string]::IsNullOrWhiteSpace($FeeScreenSimRoot)) {
    Select-FeeScreenSimRoot
}
else {
    Resolve-FeeScreenSimRoot -ExplicitRoot $FeeScreenSimRoot
}

if (-not $DoNotPersistFeeSdk) {
    [Environment]::SetEnvironmentVariable('FEE_SCREEN_SIM_ROOT', $feeRoot, 'User')
    $env:FEE_SCREEN_SIM_ROOT = $feeRoot
}

Push-Location $repositoryRoot
try {
    if (-not [string]::IsNullOrWhiteSpace($AdditionalPackageSource)) {
        $packageSource = (Resolve-Path -LiteralPath $AdditionalPackageSource).Path
        dotnet restore VIBN_Tools_App.sln "-p:RestoreAdditionalProjectSources=$packageSource"
    }
    else {
        dotnet restore VIBN_Tools_App.sln
    }
    Assert-LastExitCode 'NuGet-Wiederherstellung'
}
finally {
    Pop-Location
}

Write-Host "Entwicklungsumgebung vorbereitet. FEE SDK: $feeRoot"
if (-not $DoNotPersistFeeSdk) {
    Write-Host 'Visual Studio neu starten, damit FEE_SCREEN_SIM_ROOT übernommen wird.'
}
