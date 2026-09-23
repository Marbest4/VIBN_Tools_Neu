function Get-InstalledFeeScreenSimRoots {
    [CmdletBinding()]
    param()

    $installationRoots = [Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $installationRoots.Add((Join-Path $env:ProgramFiles 'fe.screen-sim V5'))
    }
    if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
        $installationRoots.Add((Join-Path ${env:ProgramFiles(x86)} 'fe.screen-sim V5'))
    }

    $detected = [Collections.Generic.List[object]]::new()
    foreach ($installationRoot in ($installationRoots | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $installationRoot -PathType Container)) { continue }
        foreach ($directory in (Get-ChildItem -LiteralPath $installationRoot -Directory)) {
            $match = [regex]::Match($directory.Name, '\d+(?:\.\d+){1,3}')
            $parsed = [version]'0.0'
            if ($match.Success) { [void][version]::TryParse($match.Value, [ref]$parsed) }
            $detected.Add([pscustomobject]@{ Path = $directory.FullName; Version = $parsed })
        }
    }

    return @($detected |
        Sort-Object Version -Descending |
        Select-Object -ExpandProperty Path -Unique)
}

function Get-CompleteInstalledFeeScreenSimRoots {
    [CmdletBinding()]
    param()

    $complete = [Collections.Generic.List[string]]::new()
    foreach ($candidate in (Get-InstalledFeeScreenSimRoots)) {
        if (Test-Path -LiteralPath (Join-Path $candidate 'Bin\FS.SDK.dll') -PathType Leaf) {
            $complete.Add((Resolve-Path -LiteralPath $candidate).Path)
        }
        else {
            Write-Warning "FEE-Installation '$candidate' wird übersprungen: Bin\FS.SDK.dll fehlt."
        }
    }
    return @($complete)
}

function Get-FeeScreenSimBinPath {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$FeeRoot)

    $absoluteRoot = [IO.Path]::GetFullPath($FeeRoot)
    $installedBin = Join-Path $absoluteRoot 'Bin'
    if (Test-Path -LiteralPath (Join-Path $installedBin 'FS.SDK.dll') -PathType Leaf) {
        return $installedBin
    }
    if (Test-Path -LiteralPath (Join-Path $absoluteRoot 'FS.SDK.dll') -PathType Leaf) {
        return $absoluteRoot
    }
    return $null
}

function Get-FeeReadingUnitPluginPath {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$FeeBinPath)

    $installedPlugin = Join-Path $FeeBinPath 'Plugins\ReadingUnitPlugin\ReadingUnitPlugin.dll'
    if (Test-Path -LiteralPath $installedPlugin -PathType Leaf) { return $installedPlugin }
    $flatPlugin = Join-Path $FeeBinPath 'ReadingUnitPlugin.dll'
    if (Test-Path -LiteralPath $flatPlugin -PathType Leaf) { return $flatPlugin }
    return $null
}

function Resolve-FeeScreenSimRoot {
    [CmdletBinding()]
    param([string]$ExplicitRoot)

    $repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
    $candidates = [Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($ExplicitRoot)) { $candidates.Add($ExplicitRoot) }
    if (-not [string]::IsNullOrWhiteSpace($env:FEE_SCREEN_SIM_ROOT)) { $candidates.Add($env:FEE_SCREEN_SIM_ROOT) }
    foreach ($installedRoot in (Get-InstalledFeeScreenSimRoots)) { $candidates.Add($installedRoot) }
    # The repository-local SDK is a deterministic CI/test fallback, not a
    # reason to ignore a newer complete product installation.
    $candidates.Add((Join-Path $repositoryRoot 'external\fe-screen-sim'))
    $candidates.Add((Join-Path $repositoryRoot 'SDK'))

    $checked = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        $absoluteCandidate = [IO.Path]::GetFullPath($candidate)
        if (-not $checked.Add($absoluteCandidate)) { continue }

        $binPath = Get-FeeScreenSimBinPath -FeeRoot $absoluteCandidate
        if (-not [string]::IsNullOrWhiteSpace($binPath)) {
            $resolved = (Resolve-Path -LiteralPath $absoluteCandidate).Path
            $sdkVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $binPath 'FS.SDK.dll')).FileVersion
            Write-Host "FEE SDK erkannt: Version $sdkVersion unter '$resolved'."
            return $resolved
        }

        if (Test-Path -LiteralPath $absoluteCandidate -PathType Container) {
            Write-Warning "FEE-SDK '$absoluteCandidate' wird übersprungen: FS.SDK.dll fehlt in Bin oder im Stammordner."
        }
    }

    throw 'Kein vollständiges fe.screen-sim-SDK gefunden. FEE_SCREEN_SIM_ROOT setzen oder external\fe-screen-sim beziehungsweise SDK bereitstellen.'
}

function Select-FeeScreenSimRoot {
    [CmdletBinding()]
    param()

    $complete = @(Get-CompleteInstalledFeeScreenSimRoots)
    if ($complete.Count -eq 0) {
        return (Resolve-FeeScreenSimRoot)
    }
    if ($complete.Count -eq 1) {
        Write-Host "Eine vollständige FEE-SDK-Version erkannt: $($complete[0])"
        return $complete[0]
    }

    Write-Host 'Mehrere vollständige FEE-SDK-Versionen wurden erkannt:'
    for ($index = 0; $index -lt $complete.Count; $index++) {
        $defaultText = if ($index -eq 0) { ' (neueste, Standard)' } else { '' }
        Write-Host "  [$($index + 1)] $($complete[$index])$defaultText"
    }
    $answer = Read-Host 'SDK für Visual Studio und Build auswählen [1]'
    if ([string]::IsNullOrWhiteSpace($answer)) { return $complete[0] }

    $selection = 0
    if (-not [int]::TryParse($answer, [ref]$selection) -or
        $selection -lt 1 -or $selection -gt $complete.Count) {
        throw "Ungültige SDK-Auswahl '$answer'."
    }
    return $complete[$selection - 1]
}

function Assert-LastExitCode {
    param([string]$Operation)
    if ($LASTEXITCODE -ne 0) { throw "$Operation ist mit Exitcode $LASTEXITCODE fehlgeschlagen." }
}

function Get-FeeRuntimeClosure {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$FeeRoot)

    $repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
    $binRoot = Get-FeeScreenSimBinPath -FeeRoot $FeeRoot
    if ([string]::IsNullOrWhiteSpace($binRoot)) {
        throw "Im FEE-SDK '$FeeRoot' fehlt FS.SDK.dll in Bin oder im Stammordner."
    }
    $pluginAssembly = Get-FeeReadingUnitPluginPath -FeeBinPath $binRoot
    $availableFiles = @(Get-ChildItem -LiteralPath $binRoot -Filter 'FS.*.dll' -File)
    if ($availableFiles.Count -eq 0) {
        throw "Im FEE-SDK '$FeeRoot' wurden keine FS.*-Runtime-Assemblies gefunden."
    }
    if ([string]::IsNullOrWhiteSpace($pluginAssembly)) {
        throw "Im FEE-SDK '$FeeRoot' fehlt ReadingUnitPlugin.dll im Pluginpfad oder im flachen SDK-Ordner."
    }

    $available = [Collections.Generic.Dictionary[string, IO.FileInfo]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($assemblyFile in $availableFiles) {
        $available[[IO.Path]::GetFileNameWithoutExtension($assemblyFile.Name)] = $assemblyFile
    }

    # The project references are the closure roots. Starting from them avoids
    # packaging unrelated FS tools merely because they share the same Bin dir.
    [xml]$project = Get-Content -LiteralPath (Join-Path $repositoryRoot 'VIBN_Tools.csproj') -Raw
    $rootNames = @($project.SelectNodes('//Reference') |
        ForEach-Object { [string]$_.Include } |
        Where-Object { $_.StartsWith('FS.', [StringComparison]::OrdinalIgnoreCase) } |
        Sort-Object -Unique)
    $queue = [Collections.Generic.Queue[IO.FileInfo]]::new()
    $selected = [Collections.Generic.Dictionary[string, IO.FileInfo]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($rootName in $rootNames) {
        if (-not $available.ContainsKey($rootName)) {
            throw "Die im Projekt referenzierte FEE-Assembly '$rootName.dll' fehlt unter '$binRoot'."
        }
        $rootFile = $available[$rootName]
        $selected[$rootName] = $rootFile
        $queue.Enqueue($rootFile)
    }
    $queue.Enqueue((Get-Item -LiteralPath $pluginAssembly))

    $inspected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $missing = [Collections.Generic.SortedSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    while ($queue.Count -gt 0) {
        $assemblyFile = $queue.Dequeue()
        if (-not $inspected.Add($assemblyFile.FullName)) { continue }
        try {
            $assembly = [Reflection.Assembly]::LoadFile($assemblyFile.FullName)
            foreach ($reference in $assembly.GetReferencedAssemblies()) {
                if (-not $reference.Name.StartsWith('FS.', [StringComparison]::OrdinalIgnoreCase)) { continue }
                if (-not $available.ContainsKey($reference.Name)) {
                    [void]$missing.Add($reference.Name)
                    continue
                }
                $dependencyFile = $available[$reference.Name]
                if (-not $selected.ContainsKey($reference.Name)) {
                    $selected[$reference.Name] = $dependencyFile
                    $queue.Enqueue($dependencyFile)
                }
            }
        }
        catch {
            throw "Benötigte FEE-Assembly konnte nicht geprüft werden: $($assemblyFile.FullName). $($_.Exception.Message)"
        }
    }

    if ($missing.Count -gt 0) {
        throw "Das FEE-SDK ist nur für den Build, nicht für ein lauffähiges Deployment vollständig. Fehlend: $($missing -join ', ')."
    }

    return @($selected.Values | Sort-Object Name)
}

function Assert-FeeRuntimeClosure {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$FeeRoot)

    [void]@(Get-FeeRuntimeClosure -FeeRoot $FeeRoot)
}
