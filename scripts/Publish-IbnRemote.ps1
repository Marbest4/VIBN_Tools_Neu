[CmdletBinding()]
param(
    [string]$OutputDirectory = "",
    [string]$InWorkFilter = "",
    [string]$ApiKey = "",
    [string]$RemoteDesktopPassword = "",
    [switch]$PasteFriendlyCredentials
)

$ErrorActionPreference = 'Stop'

function Read-SecretValue {
    param([Parameter(Mandatory = $true)][string]$Prompt)

    if ($PasteFriendlyCredentials) {
        Write-Warning "$Prompt wird sichtbar eingegeben. Der Wert landet nicht in der PowerShell-History, ist aber auf dem Bildschirm lesbar."
        return Read-Host "$Prompt (Einfügen mit Strg+V möglich)"
    }

    $secureValue = Read-Host $Prompt -AsSecureString
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureValue)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
        $secureValue.Dispose()
    }
}

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
if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    $ApiKey = Read-SecretValue 'Kanbanize/Businessmap API-Key'
}
if ([string]::IsNullOrWhiteSpace($RemoteDesktopPassword)) {
    $RemoteDesktopPassword = Read-SecretValue 'Gemeinsames Remote-Desktop-Passwort'
}
if ([string]::IsNullOrWhiteSpace($ApiKey) -or [string]::IsNullOrWhiteSpace($RemoteDesktopPassword)) {
    throw 'API-Key und Remote-Desktop-Passwort dürfen nicht leer sein.'
}
if ($ApiKey.IndexOfAny([char[]]"`r`n") -ge 0 -or $RemoteDesktopPassword.IndexOfAny([char[]]"`r`n") -ge 0) {
    throw 'API-Key und Remote-Desktop-Passwort dürfen keinen Zeilenumbruch enthalten.'
}
if ($ApiKey -ne '12345' -or $RemoteDesktopPassword -ne '67890') {
    Write-Warning 'Die übergebenen Zugangsdaten werden in die EXE eingebettet und sind aus der Binärdatei extrahierbar. Nur für kontrollierte Verteilung verwenden.'
}
$usesPlaceholders = $ApiKey -eq '12345' -and $RemoteDesktopPassword -eq '67890'

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
$previousApiKey = [Environment]::GetEnvironmentVariable('IbnRemoteApiKey', 'Process')
$previousRemoteDesktopPassword = [Environment]::GetEnvironmentVariable('IbnRemoteRemoteDesktopPassword', 'Process')
try {
    # MSBuild imports process environment variables as properties. This keeps
    # the entered values out of the dotnet command line and shell history.
    [Environment]::SetEnvironmentVariable('IbnRemoteApiKey', $ApiKey, 'Process')
    [Environment]::SetEnvironmentVariable('IbnRemoteRemoteDesktopPassword', $RemoteDesktopPassword, 'Process')
    & dotnet @publishArguments

    if ($LASTEXITCODE -ne 0) {
        throw "IBN-Publish ist mit ExitCode $LASTEXITCODE fehlgeschlagen."
    }
}
finally {
    [Environment]::SetEnvironmentVariable('IbnRemoteApiKey', $previousApiKey, 'Process')
    [Environment]::SetEnvironmentVariable('IbnRemoteRemoteDesktopPassword', $previousRemoteDesktopPassword, 'Process')
    $ApiKey = $null
    $RemoteDesktopPassword = $null
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
if ($usesPlaceholders) {
    Write-Host 'Die angeforderten ungültigen Platzhalter 12345/67890 sind in der EXE eingebettet.'
}
else {
    Write-Warning 'Benutzerdefinierte Zugangsdaten sind in der erzeugten EXE enthalten und nicht als Geheimnis geschützt.'
}
