[CmdletBinding()]
param(
    [string]$OutputDirectory = "",
    [string]$InWorkFilter = "",
    [Security.SecureString]$KanbanizeApiKey,
    [Security.SecureString]$RemoteDesktopPassword,
    [switch]$SkipCredentialConfiguration
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

if (-not $SkipCredentialConfiguration) {
    if ($null -eq $KanbanizeApiKey) {
        $KanbanizeApiKey = Read-Host 'Kanbanize API-Key (geschützte Eingabe)' -AsSecureString
    }
    if ($null -eq $RemoteDesktopPassword) {
        $RemoteDesktopPassword = Read-Host 'RDP-Passwort (geschützte Eingabe)' -AsSecureString
    }

    $apiPointer = [IntPtr]::Zero
    $passwordPointer = [IntPtr]::Zero
    try {
        $apiPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($KanbanizeApiKey)
        $passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($RemoteDesktopPassword)
        $payload = [PSCustomObject]@{
            KanbanizeApiKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($apiPointer)
            RemoteDesktopPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)
        } | ConvertTo-Json -Compress

        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $executable
        $startInfo.ArgumentList.Add('--configure-stdin')
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $startInfo.RedirectStandardInput = $true
        $startInfo.RedirectStandardError = $true
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $startInfo
        if (-not $process.Start()) {
            throw 'Die veröffentlichte IBN-EXE konnte für die geschützte Konfiguration nicht gestartet werden.'
        }
        $process.StandardInput.Write($payload)
        $process.StandardInput.Close()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) {
            throw "Die geschützte IBN-Konfiguration ist mit ExitCode $($process.ExitCode) fehlgeschlagen: $($process.StandardError.ReadToEnd())"
        }
    }
    finally {
        if ($apiPointer -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($apiPointer)
        }
        if ($passwordPointer -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
        }
        $payload = $null
    }
}

$unexpected = @(Get-ChildItem -LiteralPath $OutputDirectory -File | Where-Object Name -ne 'VIBN_Tools_IBN.exe')
if ($unexpected.Count -gt 0) {
    Write-Warning "Zusätzliche Publish-Dateien gefunden: $($unexpected.Name -join ', ')"
}

Write-Host "IBN Remote bereit: $executable"
Write-Host "In-Arbeit-Filter: $InWorkFilter"
if (-not $SkipCredentialConfiguration) {
    Write-Host 'API-Key und RDP-Passwort wurden für den aktuellen Windows-Benutzer im Credential Manager gespeichert.'
}
