[CmdletBinding()]
param(
    [string]$FeeScreenSimRoot,
    [string]$AdditionalPackageSource,
    [string]$InnoCompiler
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path

& (Join-Path $PSScriptRoot 'Publish-Portable.ps1') `
    -FeeScreenSimRoot $FeeScreenSimRoot `
    -AdditionalPackageSource $AdditionalPackageSource `
    -SkipArchive
if ($LASTEXITCODE -ne 0) { throw 'Portable Veröffentlichung fehlgeschlagen.' }

$compilerCandidates = @(
    $InnoCompiler,
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

$compiler = $compilerCandidates |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1
if ($null -eq $compiler) {
    throw 'Inno Setup 6 wurde nicht gefunden. InnoCompiler angeben oder Inno Setup 6 installieren.'
}

Push-Location $repositoryRoot
try {
    & $compiler 'installer\VIBN_Tools.iss'
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup ist mit Exitcode $LASTEXITCODE fehlgeschlagen." }
}
finally {
    Pop-Location
}

Write-Host (Join-Path $repositoryRoot 'artifacts\installer\VIBN_Tools_Setup.exe')
