[CmdletBinding()]
param(
    [string]$OutputDirectory = "",
    [string]$InWorkFilter = ""
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'VIBN_Tools.IbnRemote\VIBN_Tools.IbnRemote.csproj'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts\publish\IBN-Remote'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ([string]::IsNullOrWhiteSpace($InWorkFilter)) {
    $InWorkFilter = Read-Host 'Fester Filter für die Spalte In Arbeit'
}
$InWorkFilter = $InWorkFilter.Trim()
if ([string]::IsNullOrWhiteSpace($InWorkFilter)) {
    throw 'Der In-Arbeit-Filter darf nicht leer sein.'
}
if ($InWorkFilter.IndexOfAny([char[]]";`r`n") -ge 0) {
    throw 'Der In-Arbeit-Filter darf kein Semikolon und keinen Zeilenumbruch enthalten.'
}

$publishArguments = @(
    'publish', $project,
    '--configuration', 'Release',
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--output', $OutputDirectory,
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=embedded',
    '-p:DebugSymbols=false',
    "-p:IbnRemoteInWorkFilter=$InWorkFilter"
)
& dotnet @publishArguments

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
Write-Host "In-Arbeit-Filter: $InWorkFilter"
Write-Host 'Die angeforderten ungültigen Platzhalter 12345/67890 sind in der EXE eingebettet.'
