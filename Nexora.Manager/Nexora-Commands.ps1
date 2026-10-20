# Nexora Manager - commandes locales (Windows PowerShell 5.1)
# N'affecte pas les pilotes ni les identifiants CTAP2.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [ValidateSet('Build','Tests','Publish','Installer','Doctor','USBIP','RunInstalled','RunDev','Stop','OpenLogs','OpenData','OpenProject','BackupData')]
    [string]$Action,
    [string]$Repository = (Split-Path $PSScriptRoot -Parent),
    [switch]$ForceStop
)
$ErrorActionPreference = 'Stop'

function Title([string]$message) {
    Write-Host "`n=== $message ===" -ForegroundColor Cyan
}
function Fail([string]$message) {
    throw $message
}
function Validate-Project() {
    if (!(Test-Path -LiteralPath (Join-Path $Repository 'Nexora.Gui\Nexora.Gui.csproj'))) {
        Fail "Depot Nexora introuvable : $Repository"
    }
}
function Get-RunningApps() {
    @(Get-Process -Name Nexora -ErrorAction SilentlyContinue)
}
function Assert-NotRunning() {
    $apps = @(Get-RunningApps)
    if ($apps.Count -gt 0) {
        Fail ("Nexora est actif (PID : " + (($apps | ForEach-Object {$_.Id}) -join ', ') +
              "). Quitter depuis l'icone de notification Windows avant de compiler/sauvegarder.")
    }
}
function Get-InstalledExe() {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Nexora\Nexora.exe')
    )
    $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
function Invoke-Dotnet([string[]]$Arguments) {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Fail "SDK .NET introuvable. Installer .NET SDK 8 avant de compiler."
    }
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { Fail "dotnet a termine avec le code $LASTEXITCODE." }
}
function Get-UsbipExe() {
    Join-Path $env:ProgramFiles 'USBip\usbip.exe'
}
function Get-DataLocations() {
    $items = @(
        [pscustomobject]@{Label='DevRelease';Path=(Join-Path $Repository 'Nexora.Gui\bin\Release\net8.0\Nexora')},
        [pscustomobject]@{Label='DevDebug';Path=(Join-Path $Repository 'Nexora.Gui\bin\Debug\net8.0\Nexora')},
        [pscustomobject]@{Label='Published';Path=(Join-Path $Repository 'dist\Nexora\Nexora')},
        [pscustomobject]@{Label='Installed';Path=(Join-Path $env:LOCALAPPDATA 'Programs\Nexora\Nexora')},
        [pscustomobject]@{Label='InterfaceSettings';Path=(Join-Path $env:LOCALAPPDATA 'Nexora')}
    )
    @($items | Where-Object { Test-Path -LiteralPath $_.Path -PathType Container })
}
function Open-Folder([string]$path) {
    if (!(Test-Path -LiteralPath $path)) { Fail "Dossier introuvable : $path" }
    Start-Process explorer.exe -ArgumentList ('"' + $path + '"')
}
function Backup-Data() {
    Assert-NotRunning
    $locations = @(Get-DataLocations)
    if ($locations.Count -eq 0) {
        Fail "Aucun dossier de donnees trouve. Verifiez la version Nexora utilisee."
    }
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    [System.Windows.Forms.MessageBox]::Show(
        "Cette sauvegarde contiendra potentiellement des CLES PRIVEES et des informations sensibles.`n" +
        "Le ZIP NE SERA PAS chiffre. Placez-le sur un disque chiffre et ne le partagez pas.`n" +
        "Fermez Nexora avant la sauvegarde (deja verifie).",
        'Nexora - Sauvegarde sensible',
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Warning
    ) | Out-Null

    $dialog = New-Object System.Windows.Forms.SaveFileDialog
    $dialog.Title = 'Enregistrer une sauvegarde des donnees Nexora'
    $dialog.Filter = 'Archive ZIP (*.zip)|*.zip'
    $dialog.FileName = ('Nexora_Donnees_PRIVEES_' + (Get-Date -Format 'yyyyMMdd_HHmmss') + '.zip')
    if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
        Write-Host 'Sauvegarde annulee.'
        return
    }
    $output = $dialog.FileName
    if (Test-Path -LiteralPath $output) { Fail "Le fichier existe deja, choisissez un autre nom : $output" }
    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ('NexoraBackup_' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    try {
        foreach ($location in $locations) {
            $dest = Join-Path $temp $location.Label
            Copy-Item -LiteralPath $location.Path -Destination $dest -Recurse -Force
            Write-Host ("Copie : " + $location.Label) -ForegroundColor Green
        }
        $note = @"
NEXORA - SAUVEGARDE CONFIDENTIELLE (non chiffree)
Date (local) : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
Contenu : dossiers de donnees FIDO2 presents et preferences de l'interface.
Chaque sous-dossier conserve une origine reconnaissable par son libelle.
ATTENTION : cette sauvegarde peut contenir des secrets cryptographiques.
Restaurer seulement vers le bon emplacement, Nexora ferme, apres identification de la version installee.
Ne pas confondre cette archive avec le code source public.
"@
        Set-Content -LiteralPath (Join-Path $temp 'IMPORTANT.txt') -Value $note -Encoding UTF8
        [System.IO.Compression.ZipFile]::CreateFromDirectory(
            $temp, $output, [System.IO.Compression.CompressionLevel]::Optimal, $false)
        Write-Host "Sauvegarde creee : $output" -ForegroundColor Green
        Write-Host 'IMPORTANT : sauvegarde NON CHIFFREE. Gardez-la privee.' -ForegroundColor Yellow
    }
    finally {
        Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

try {
    $Repository = (Resolve-Path -LiteralPath $Repository).Path
    Validate-Project
    Set-Location -LiteralPath $Repository

    switch ($Action) {
        'Build' {
            Title 'Compilation Release (.NET 8)'
            Assert-NotRunning
            Invoke-Dotnet @('build', (Join-Path $Repository 'Nexora.sln'), '-c', 'Release')
            Write-Host 'Compilation terminee.' -ForegroundColor Green
        }
        'Tests' {
            Title 'Tests du projet'
            Assert-NotRunning
            Invoke-Dotnet @('test', (Join-Path $Repository 'Nexora.sln'), '-c', 'Release')
        }
        'Publish' {
            Title 'Publication de Nexora.exe'
            Assert-NotRunning
            & (Join-Path (Split-Path $Repository -Parent) 'nexora-installer\publish.ps1')
            if (-not $?) { Fail 'Echec du script de publication.' }
            Write-Host (Join-Path $Repository 'dist\Nexora\Nexora.exe') -ForegroundColor Green
        }
        'Installer' {
            Title 'Creation du programme installation'
            Assert-NotRunning
            & (Join-Path (Split-Path $Repository -Parent) 'nexora-installer\publish.ps1') -MakeInstaller
            if (-not $?) { Fail "Echec de generation de l'installateur." }
            Write-Host (Join-Path $Repository 'dist\Nexora-Setup.exe') -ForegroundColor Green
        }
        'RunInstalled' {
            Title 'Lancement de la version installee'
            if ((Get-RunningApps).Count -gt 0) { Fail 'Nexora fonctionne deja.' }
            $appExe = Get-InstalledExe
            if (-not $appExe) {
                Fail "Aucune installation Nexora detectee dans AppData\Programs. Publiez puis installez, ou lancez la version de developpement."
            }
            Start-Process -FilePath $appExe -WorkingDirectory (Split-Path $appExe -Parent)
            Write-Host "Application lancee : $appExe" -ForegroundColor Green
        }
        'RunDev' {
            Title 'Lancement de la version de developpement'
            if ((Get-RunningApps).Count -gt 0) { Fail 'Nexora fonctionne deja.' }
            $appExe = Join-Path $Repository 'Nexora.Gui\bin\Release\net8.0\Nexora.exe'
            if (!(Test-Path -LiteralPath $appExe)) { Fail 'Compilez le projet avant de lancer la version de developpement.' }
            Write-Host "ATTENTION : donnees FIDO2 localisees a cote de CET executable." -ForegroundColor Yellow
            Start-Process -FilePath $appExe -WorkingDirectory (Split-Path $appExe -Parent)
        }
        'Stop' {
            Title 'Arret demande'
            if (-not $ForceStop) {
                Fail "Quittez d'abord Nexora depuis son icone de notification. Pour un arret force, utiliser explicitement -ForceStop."
            }
            $apps = @(Get-RunningApps)
            foreach ($app in $apps) {
                Write-Host ("Arret force du PID " + $app.Id) -ForegroundColor Yellow
                Stop-Process -Id $app.Id -Force -ErrorAction Stop
            }
            Write-Host "Nexora arrete. Verifiez les ports USB/IP si une cle est encore affichee."
        }
        'USBIP' {
            Title 'USB/IP - statut seulement (aucune modification)'
            $usbip = Get-UsbipExe
            if (Test-Path -LiteralPath $usbip) {
                & $usbip port
            } else {
                Write-Host "usbip.exe absent : $usbip" -ForegroundColor Yellow
            }
            if (Get-Command Get-PnpDevice -ErrorAction SilentlyContinue) {
                Get-PnpDevice -ErrorAction SilentlyContinue |
                    Where-Object { $_.FriendlyName -match 'fido' -or $_.InstanceId -match 'VID_1953&PID_1337' } |
                    Select-Object Status,Class,FriendlyName,InstanceId | Format-Table -AutoSize
            }
        }
        'Doctor' {
            Title 'Diagnostics non destructifs'
            Write-Host "Projet  : $Repository"
            Write-Host "Version fichier : $(Get-Content -LiteralPath (Join-Path $Repository 'version.txt') -ErrorAction SilentlyContinue)"
            $sdk = Get-Command dotnet -ErrorAction SilentlyContinue
            Write-Host ('dotnet SDK : ' + $(if($sdk){'present'}else{'ABSENT'}))
            if($sdk){ & dotnet --version }
            $usbip=Get-UsbipExe
            Write-Host ('usbip.exe : ' + $(if(Test-Path -LiteralPath $usbip){'present'}else{'ABSENT'}))
            Write-Host ('Installateur Inno Setup : ' +
                $(if (Test-Path 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe') {'present'} elseif (Test-Path 'C:\Program Files\Inno Setup 6\ISCC.exe') {'present'} else {'ABSENT'}))
            Write-Host "Instances Nexora : $(@(Get-RunningApps).Count)"
            $locations = @(Get-DataLocations)
            Write-Host "Dossiers de donnees detectes : $($locations.Count)"
            foreach($loc in $locations) { Write-Host " - $($loc.Label): $($loc.Path)" }
            Write-Host "Aucune modification du pilote USB/IP." -ForegroundColor Cyan
        }
        'OpenLogs' {
            Title 'Journaux de Nexora'
            $candidates = @(
                (Join-Path $env:LOCALAPPDATA 'Nexora'),
                (Join-Path $Repository 'Nexora.Gui\bin\Release\net8.0\logs'),
                (Join-Path $Repository 'dist\Nexora\logs'),
                (Join-Path $env:LOCALAPPDATA 'Programs\Nexora\logs')
            )
            $existing = @($candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Container })
            if ($existing.Count -eq 0) { Fail 'Aucun dossier de logs trouve.' }
            foreach ($dir in $existing) { Write-Host $dir }
            Open-Folder $existing[0]
        }
        'OpenData' {
            Title 'Emplacements des donnees - ouverture seulement'
            $locations = @(Get-DataLocations)
            if ($locations.Count -eq 0) { Fail 'Aucun dossier de donnees detecte.' }
            foreach ($loc in $locations) { Write-Host "$($loc.Label) : $($loc.Path)" }
            Open-Folder $locations[0].Path
        }
        'OpenProject' { Open-Folder $Repository }
        'BackupData' { Title 'Sauvegarde des donnees confidentielles'; Backup-Data }
    }
    Write-Host "`nCommande terminee." -ForegroundColor Green
}
catch {
    Write-Host ("ERREUR : " + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
