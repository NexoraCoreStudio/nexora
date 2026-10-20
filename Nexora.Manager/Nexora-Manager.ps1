# Nexora Manager - GUI locale WinForms pour Windows PowerShell 5.1.
# Lance les actions dans des consoles distinctes. Ne manipule pas les secrets FIDO2.
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$commandsScript = Join-Path $PSScriptRoot 'Nexora-Commands.ps1'
if (!(Test-Path -LiteralPath (Join-Path $projectRoot 'Nexora.Gui\Nexora.Gui.csproj'))) {
    [System.Windows.Forms.MessageBox]::Show('Projet Nexora.Gui introuvable.', 'Nexora Manager') | Out-Null
    exit 1
}
if (!(Test-Path -LiteralPath $commandsScript)) { exit 1 }

function Color([string]$html) {
    [System.Drawing.ColorTranslator]::FromHtml($html)
}

$form = New-Object System.Windows.Forms.Form
$form.Text = 'Nexora Manager'
$form.Size = New-Object System.Drawing.Size(1040,720)
$form.MinimumSize = New-Object System.Drawing.Size(1040,720)
$form.MaximumSize = New-Object System.Drawing.Size(1040,720)
$form.StartPosition = 'CenterScreen'
$form.BackColor = Color '#0B1020'
$form.ForeColor = Color '#EDF3FF'
$form.Font = New-Object System.Drawing.Font('Segoe UI',10)
$form.FormBorderStyle = 'FixedSingle'
$form.MaximizeBox = $false
$form.AutoScaleMode = [System.Windows.Forms.AutoScaleMode]::Dpi

$icon = Join-Path $projectRoot 'Nexora.Gui\Assets\Nexora_Icon.ico'
if (Test-Path -LiteralPath $icon) {
    try { $form.Icon = New-Object System.Drawing.Icon($icon) } catch { }
}

function Make-Label(
    [string]$text, [int]$x, [int]$y, [int]$width, [int]$height,
    [int]$fontSize = 10, [bool]$bold=$false, [string]$color='#B9C8E6'
) {
    $lab = New-Object System.Windows.Forms.Label
    $lab.Text = $text
    $lab.Location = New-Object System.Drawing.Point($x,$y)
    $lab.Size = New-Object System.Drawing.Size($width,$height)
    $style = if ($bold) { [System.Drawing.FontStyle]::Bold } else { [System.Drawing.FontStyle]::Regular }
    $lab.Font = New-Object System.Drawing.Font('Segoe UI',$fontSize,$style)
    $lab.ForeColor = Color $color
    $lab.BackColor = [System.Drawing.Color]::Transparent
    $form.Controls.Add($lab)
    return $lab
}

Make-Label 'NEXORA' 28 20 390 51 28 $true '#F5F6FF' | Out-Null
Make-Label 'MANAGER  /  SYSTEM CONTROL' 32 72 500 24 10 $true '#9A8DFF' | Out-Null
Make-Label 'Gerer Nexora, la compilation, USB/IP, les tests et les sauvegardes.' 32 105 850 25 11 $false '#C5D0E4' | Out-Null

$separator = New-Object System.Windows.Forms.Panel
$separator.Location = New-Object System.Drawing.Point(32,144)
$separator.Size = New-Object System.Drawing.Size(960,2)
$separator.BackColor = Color '#6B47D1'
$form.Controls.Add($separator)

$statusApp = Make-Label 'Application : verification...' 35 164 400 30 11 $true '#67D9C4'
$statusUSB = Make-Label 'USB/IP : verification...' 460 164 360 30 11 $true '#E5C079'
$statusSDK = Make-Label '.NET SDK : verification...' 35 195 420 25 10 $false '#B9C8E6'
$statusVersion = Make-Label 'Nexora · installation v1.1' 460 195 360 25 10 $false '#B9C8E6'

Make-Label 'EXECUTION' 35 234 230 25 10 $true '#A99AFF' | Out-Null
Make-Label 'DEVELOPPEMENT' 364 234 230 25 10 $true '#A99AFF' | Out-Null
Make-Label 'MAINTENANCE' 692 234 230 25 10 $true '#A99AFF' | Out-Null

function Start-Command([string]$action, [bool]$forceStop = $false) {
    $arguments = @(
        '-NoProfile', '-NoExit', '-STA',
        '-ExecutionPolicy', 'RemoteSigned',
        '-File', ('"' + $commandsScript + '"'),
        '-Action', $action,
        '-Repository', ('"' + $projectRoot + '"')
    )
    if ($forceStop) { $arguments += '-ForceStop' }
    try {
        Start-Process -FilePath 'powershell.exe' -WorkingDirectory $projectRoot -ArgumentList $arguments -ErrorAction Stop | Out-Null
        $lastAction.Text = "Commande ouverte : $action  |  consulter la console PowerShell."
    }
    catch {
        [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Nexora Manager') | Out-Null
    }
}

function Make-Button(
    [string]$title,[string]$description,[string]$action,
    [int]$col,[int]$row,[bool]$accent=$false
) {
    $x = 34 + ($col * 328)
    $y = 269 + ($row * 76)
    $btn = New-Object System.Windows.Forms.Button
    $btn.Location = New-Object System.Drawing.Point($x,$y)
    $btn.Size = New-Object System.Drawing.Size(300,62)
    $btn.Text = $title + "`n" + $description
    $btn.Font = New-Object System.Drawing.Font('Segoe UI',9.5,[System.Drawing.FontStyle]::Regular)
    $btn.BackColor = if($accent){ Color '#5B36D5' }else{ Color '#1B2944' }
    $btn.ForeColor = Color '#F4F5FF'
    $btn.FlatStyle = 'Flat'
    $btn.FlatAppearance.BorderSize = 1
    $btn.FlatAppearance.BorderColor = Color '#3C4E78'
    $btn.TextAlign = 'MiddleCenter'
    $btn.Cursor = [System.Windows.Forms.Cursors]::Hand
    $btn.Tag = $action
    $btn.Add_Click({
        $chosen = [string]$this.Tag
        if ($chosen -eq 'Stop') {
            $answer = [System.Windows.Forms.MessageBox]::Show(
                "La fermeture forcee peut interrompre une authentification USB/IP.`n" +
                "Utilisez plutot Quitter dans la zone de notification.`n`nForcer l'arret maintenant ?",
                'Confirmation - Nexora',
                [System.Windows.Forms.MessageBoxButtons]::YesNo,
                [System.Windows.Forms.MessageBoxIcon]::Warning)
            if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { return }
            Start-Command 'Stop' $true
        } elseif ($chosen -eq 'BackupData') {
            $answer = [System.Windows.Forms.MessageBox]::Show(
                "La sauvegarde peut contenir des CLES PRIVEES. Le ZIP n'est pas chiffre.`n" +
                "Ne le partagez pas. Nexora doit etre ferme.`n`nContinuer ?",
                'Sauvegarde de securite',
                [System.Windows.Forms.MessageBoxButtons]::YesNo,
                [System.Windows.Forms.MessageBoxIcon]::Warning)
            if ($answer -eq [System.Windows.Forms.DialogResult]::Yes) { Start-Command $chosen }
        } else {
            Start-Command $chosen
        }
    })
    $form.Controls.Add($btn)
}

# Trois colonnes : lancement, build, diagnostic/backup.
Make-Button 'Demarrer (installee)' 'Utilise les donnees de l installation' 'RunInstalled' 0 0 $true
Make-Button 'Compiler le projet' 'dotnet build - Release' 'Build' 1 0 $true
Make-Button 'Diagnostic complet' 'SDK, processus, dossiers et USB/IP' 'Doctor' 2 0

Make-Button 'Demarrer (developpement)' 'Identite separee si chemin different' 'RunDev' 0 1
Make-Button 'Executer les tests' 'dotnet test - Release' 'Tests' 1 1
Make-Button 'Etat USB/IP' 'Controleur VHCI, signature, ports' 'USBIP' 2 1

Make-Button 'Arreter Nexora' 'Arret force avec confirmation' 'Stop' 0 2
Make-Button 'Publier Nexora.exe' 'Application Windows x64' 'Publish' 1 2
Make-Button 'Sauvegarder les cles' 'Archive sensible non chiffree' 'BackupData' 2 2 $true

Make-Button 'Ouvrir le projet' 'Code source et scripts' 'OpenProject' 0 3
Make-Button 'Creer l installateur' 'Inno Setup 6 requis' 'Installer' 1 3
Make-Button 'Afficher les journaux' 'Evenements et erreurs' 'OpenLogs' 2 3

Make-Button 'Ouvrir les donnees' 'Emplacements des identifiants' 'OpenData' 0 4
Make-Button 'Installer pilote USB/IP' 'Choisir un INF signe ; UAC' 'InstallDriver' 2 4

$lastAction = Make-Label 'Pret. Les commandes s executent dans des consoles separees.' 35 661 970 24 9 $false '#92A7CC'

function Refresh-Status {
    $apps = @(Get-Process -Name Nexora -ErrorAction SilentlyContinue)
    $statusApp.Text = if($apps.Count -gt 0) { "APPLICATION : ACTIF (PID $($apps[0].Id))" } else { 'APPLICATION : ARRETEE' }
    $statusApp.ForeColor = if($apps.Count -gt 0){ Color '#5CE3B4' } else { Color '#F2B679' }

    $exeUsb = Join-Path $env:ProgramFiles 'USBip\usbip.exe'
    $controller = @(Get-PnpDevice -ErrorAction SilentlyContinue |
        Where-Object { $_.InstanceId -like 'ROOT\USB\*' -and $_.FriendlyName -match 'USBip|VHCI' -and $_.Status -eq 'OK' })
    if ($controller.Count -gt 0 -and (Test-Path -LiteralPath $exeUsb)) {
        $statusUSB.Text = 'USB/IP : PILOTE ET CLIENT OK'
        $statusUSB.ForeColor = Color '#5CE3B4'
    } elseif ($controller.Count -gt 0) {
        $statusUSB.Text = 'USB/IP : PILOTE OK / CLIENT ABSENT'
        $statusUSB.ForeColor = Color '#F2B679'
    } else {
        $statusUSB.Text = 'USB/IP : PILOTE NON DETECTE'
        $statusUSB.ForeColor = Color '#F2B679'
    }
    $statusSDK.Text = if(Get-Command dotnet -ErrorAction SilentlyContinue){ '.NET SDK : DISPONIBLE' }else{ '.NET SDK : ABSENT' }
}

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 4000
$timer.Add_Tick({Refresh-Status})
$timer.Start()
$form.Add_Shown({ Refresh-Status })
$form.Add_FormClosed({ $timer.Stop(); $timer.Dispose() })
[void]$form.ShowDialog()
