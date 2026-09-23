# VIBN Tools – Start

1. ZIP vollständig in einen lokalen Ordner entpacken. Keine einzelnen DLLs oder nur die EXE herauskopieren.
2. `Configure-VIBN-Tools.cmd` bei Bedarf einmal pro Windows-Benutzer ausführen.
3. `VIBN_Tools.exe` starten.

Das `win-x64`-Paket enthält die .NET-Laufzeit. Visual Studio ist nicht erforderlich. TIA-Funktionen benötigen weiterhin eine lokal installierte, passende TIA-PublicAPI, Mitgliedschaft in `Siemens TIA Openness` und .NET Framework 4.8 für die separat mitgelieferte Bridge. FEE-Funktionen benötigen die betrieblich lizenzierte FEE-Umgebung und erreichbare Dienste.

Das Paket enthält keine API-Keys, RDP-Kennwörter, Benutzerrollen, Caches oder Protokolle. Für einen unternehmensweiten Rollout sollte das ZIP beziehungsweise ein daraus erstellter Installer digital signiert und über den internen Softwareverteilungsweg bereitgestellt werden.

Die vollständige Installations-, Installer- und Fehlerbehebungsanleitung liegt unter `docs\INSTALLATION_UND_INSTALLER.md`.
