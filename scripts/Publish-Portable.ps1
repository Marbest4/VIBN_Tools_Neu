[CmdletBinding()]
param(
    [string]$FeeScreenSimRoot,
    [string]$AdditionalPackageSource,
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [switch]$SkipArchive
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Build-Common.ps1')
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$feeRoot = Resolve-FeeScreenSimRoot -ExplicitRoot $FeeScreenSimRoot
$feeRuntimeAssemblies = @(Get-FeeRuntimeClosure -FeeRoot $feeRoot)
Write-Host "FEE-Runtimeclosure: $($feeRuntimeAssemblies.Count) FS-Assembly(s)."
$artifactsRoot = Join-Path $repositoryRoot 'artifacts\publish'
$publishRoot = Join-Path $artifactsRoot "VIBN_Tools-$Runtime"
$zipPath = Join-Path $artifactsRoot "VIBN_Tools-$Runtime.zip"
$hashPath = "$zipPath.sha256.txt"
$feeRuntimeManifest = Join-Path $artifactsRoot 'fee-runtime-assemblies.txt'

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
$feeRuntimeAssemblies.FullName | Set-Content -LiteralPath $feeRuntimeManifest -Encoding utf8
foreach ($target in @($publishRoot, $zipPath, $hashPath)) {
    $absoluteTarget = [IO.Path]::GetFullPath($target)
    if (-not $absoluteTarget.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Ungültiges Veröffentlichungsziel: $absoluteTarget"
    }
    if (Test-Path -LiteralPath $absoluteTarget) {
        Remove-Item -LiteralPath $absoluteTarget -Recurse -Force
    }
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

    # A runtime-specific assets target is required for self-contained publish.
    if (-not [string]::IsNullOrWhiteSpace($AdditionalPackageSource)) {
        dotnet restore VIBN_Tools.csproj --runtime $Runtime "-p:RestoreAdditionalProjectSources=$packageSource"
    }
    else {
        dotnet restore VIBN_Tools.csproj --runtime $Runtime
    }
    Assert-LastExitCode 'Runtime-spezifische NuGet-Wiederherstellung'

    $publishArguments = @(
        'publish', 'VIBN_Tools.csproj',
        '--configuration', 'Release',
        '--runtime', $Runtime,
        '--self-contained', 'true',
        '--output', $publishRoot,
        '--no-restore',
        '-p:PublishSingleFile=false',
        '-p:PublishReadyToRun=false',
        "-p:FEE_SCREEN_SIM_ROOT=$feeRoot",
        "-p:FeeRuntimeAssemblyManifest=$feeRuntimeManifest"
    )
    dotnet @publishArguments
    Assert-LastExitCode 'Portable Veröffentlichung'

    Copy-Item -LiteralPath 'distribution\START_HERE.md' -Destination $publishRoot
    Copy-Item -LiteralPath 'docs' -Destination (Join-Path $publishRoot 'docs') -Recurse
    if (-not $SkipArchive) {
        Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
        $hash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
        Set-Content -LiteralPath $hashPath -Value "$($hash.Hash)  $([IO.Path]::GetFileName($zipPath))" -Encoding ascii
    }
}
finally {
    Pop-Location
}

if ($SkipArchive) {
    Write-Host "Publish-Verzeichnis: $publishRoot"
}
else {
    Write-Host "Portable Paket: $zipPath"
}
