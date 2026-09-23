[CmdletBinding()]
param(
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'VIBN_Tools.IbnRemote\VIBN_Tools.IbnRemote.csproj'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts\publish\IBN-Remote'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $OutputDirectory `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=embedded `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    throw "IBN-Publish ist mit ExitCode $LASTEXITCODE fehlgeschlagen."
}

$executable = Join-Path $OutputDirectory 'VIBN_Tools_IBN.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "IBN-EXE wurde nicht erzeugt: $executable"
}

$unexpected = @(Get-ChildItem -LiteralPath $OutputDirectory -File | Where-Object Name -ne 'VIBN_Tools_IBN.exe')
if ($unexpected.Count -gt 0) {
    Write-Warning "Zusätzliche Publish-Dateien gefunden: $($unexpected.Name -join ', ')"
}

Write-Host "IBN Remote bereit: $executable"
