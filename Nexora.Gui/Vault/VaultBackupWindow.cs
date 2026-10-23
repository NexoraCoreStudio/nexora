using System.Globalization;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Nexora.Crypt;
using Nexora.Gui.Dialogs;
using Nexora.Gui.Security;
using Nexora.Gui.Services;

namespace Nexora.Gui.Vault;

/// <summary>
/// Portable password-protected backup and restore UI. Only the encrypted .vault is exported.
/// Restoration deliberately requires closing Nexora so FIDO/USB state is rebuilt on restart.
/// </summary>
internal sealed class VaultBackupWindow : Window
{
    private readonly MainWindow _main;
    private readonly IStickManager _sticks;
    private readonly VaultAccessController _vault = VaultAccessController.Instance;
    private readonly bool _restore;
    private readonly TextBox _password;
    private readonly TextBox _passwordAgain;
    private readonly TextBox _pin;
    private readonly TextBox _confirmation;
    private readonly TextBlock _status;
    private readonly Button _start;
    private bool _busy;

    public static Task ShowAsync(Window owner, MainWindow main, IStickManager sticks, bool restore) =>
        new VaultBackupWindow(main, sticks, restore).ShowDialog(owner);

    private VaultBackupWindow(MainWindow main, IStickManager sticks, bool restore)
    {
        _main = main;
        _sticks = sticks;
        _restore = restore;
        Title = restore ? "Nexora Vault · Restaurer" : "Nexora Vault · Sauvegarder";
        Width = 630;
        Height = 570;
        MinWidth = 490;
        MinHeight = 460;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush.Parse("#0B1220");
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;

        _password = PasswordBox("Mot de passe de sauvegarde (12 caractères minimum)");
        _passwordAgain = PasswordBox("Confirmer le mot de passe de sauvegarde");
        _passwordAgain.IsVisible = !restore;
        _pin = PasswordBox("PIN actuel Nexora (4 chiffres)");
        _pin.MaxLength = 4;
        _pin.IsVisible = !restore;
        _confirmation = new TextBox { Watermark = "Saisir RESTAURER pour confirmer", MaxLength = 9 };
        _confirmation.IsVisible = restore;

        _status = new TextBlock
        {
            Text = restore
                ? "Le coffre actuel sera sauvegardé avant tout remplacement."
                : "Le fichier de sauvegarde est chiffré. Il ne sera pas envoyé à GitHub.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#8EDBE9"), FontSize = 12
        };
        _start = new Button
        {
            Content = restore ? "Restaurer le coffre" : "Créer la sauvegarde",
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = Brush.Parse(restore ? "#795174" : "#8A62D4"),
            Foreground = Brushes.White, MinWidth = 175
        };
        _start.Click += Start_Click;

        var cancel = new Button { Content = "Annuler", MinWidth = 110 };
        cancel.Click += (_, _) => { if (!_busy) Close(); };

        var content = new StackPanel { Spacing = 14 };
        content.Children.Add(new TextBlock
        {
            Text = restore ? "RESTAURATION DU COFFRE" : "SAUVEGARDE CHIFFRÉE",
            FontSize = 12, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#79DEEB")
        });
        content.Children.Add(new TextBlock
        {
            Text = restore ? "Récupérer vos clés FIDO2" : "Protéger vos clés FIDO2",
            FontSize = 25, FontWeight = FontWeight.Bold, Foreground = Brushes.White
        });
        content.Children.Add(new TextBlock
        {
            Text = restore
                ? "Sélectionnez un fichier .nxbackup. Le mot de passe de sauvegarde est nécessaire pour le déchiffrer. " +
                  "Après la restauration, votre PIN Nexora d'origine reste requis. Sur un autre compte Windows, " +
                  "vous devrez également fournir le code de récupération de 256 bits."
                : "Le coffre Nexora.vault est déjà chiffré. Cette sauvegarde ajoute un second chiffrement " +
                  "AES-256-GCM avec un mot de passe indépendant. Conservez le fichier, ce mot de passe et " +
                  "votre code de récupération dans des lieux sûrs et distincts. " +
                  "Les clés USB virtuelles seront déconnectées avant la sauvegarde.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#ABBFDB"), FontSize = 13
        });
        content.Children.Add(new Border { Height = 1, Background = Brush.Parse("#30405A"), Margin = new Thickness(0, 5) });
        content.Children.Add(Heading("MOT DE PASSE DE LA SAUVEGARDE"));
        content.Children.Add(_password);
        content.Children.Add(_passwordAgain);
        if (!restore) content.Children.Add(Heading("CONFIRMATION DU COFFRE ACTUEL"));
        content.Children.Add(_pin);
        if (restore)
        {
            content.Children.Add(Heading("CONFIRMATION DE LA RESTAURATION"));
            content.Children.Add(new TextBlock
            {
                Text = "La restauration remplace le coffre actif et entraîne la fermeture de Nexora. " +
                       "Toutes les clés USB virtuelles doivent être déconnectées.",
                Foreground = Brush.Parse("#F5C2BC"), TextWrapping = TextWrapping.Wrap, FontSize = 12
            });
            content.Children.Add(_confirmation);
        }
        content.Children.Add(_status);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 12, Margin = new Thickness(0, 4, 0, 0)
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_start);
        content.Children.Add(buttons);

        Content = new ScrollViewer
        {
            Margin = new Thickness(28, 24),
            Content = content,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
    }

    private static TextBox PasswordBox(string placeholder) => new()
    {
        Watermark = placeholder, PasswordChar = '●', MaxLength = 256
    };

    private static TextBlock Heading(string title) => new()
    {
        Text = title, FontSize = 11, FontWeight = FontWeight.Bold,
        Foreground = Brush.Parse("#C9A7FF")
    };

    private async void Start_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_busy) return;
        string password = _password.Text ?? string.Empty;
        if (password.Length < 12 || password.Length > 256 || string.IsNullOrWhiteSpace(password))
        {
            _status.Text = "Choisissez un mot de passe d'au moins 12 caractères.";
            return;
        }
        if (!_restore && !string.Equals(password, _passwordAgain.Text, StringComparison.Ordinal))
        {
            _status.Text = "Les deux mots de passe ne correspondent pas.";
            return;
        }
        if (_restore && _confirmation.Text?.Trim() != "RESTAURER")
        {
            _status.Text = "Tapez RESTAURER en majuscules pour confirmer.";
            return;
        }
        string pin = _pin.Text ?? string.Empty;
        _busy = true;
        _start.IsEnabled = false;
        try
        {
            if (_restore) await RestoreAsync(password);
            else await BackupAsync(password, pin);
        }
        catch (CryptographicException)
        {
            _status.Text = "Mot de passe incorrect ou sauvegarde endommagée. Aucun coffre remplacé.";
        }
        catch (Exception ex)
        {
            _status.Text = "Opération interrompue : " + ex.Message;
        }
        finally
        {
            _password.Text = string.Empty;
            _passwordAgain.Text = string.Empty;
            _pin.Text = string.Empty;
            _busy = false;
            _start.IsEnabled = true;
        }
    }

    private async Task BackupAsync(string password, string pin)
    {
        if (!_vault.IsConfigured || !_vault.IsUnlocked)
            throw new InvalidOperationException("Déverrouillez Nexora Vault avant de le sauvegarder.");
        if (!_vault.VerifyApproval(pin, out var error))
            throw new UnauthorizedAccessException(error);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Choisir où sauvegarder Nexora Vault",
            SuggestedFileName = "Nexora-Vault-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".nxbackup",
            DefaultExtension = "nxbackup",
            FileTypeChoices = [new FilePickerFileType("Nexora Vault Backup") { Patterns = ["*.nxbackup"] }]
        });
        if (file is null) { _status.Text = "Sauvegarde annulée."; return; }
        string destination = file.TryGetLocalPath()
            ?? throw new IOException("Choisissez un chemin local ou un lecteur USB monté.");
        if (File.Exists(destination))
            throw new IOException("Ce fichier existe déjà. Choisissez un nouveau nom pour préserver l'ancienne sauvegarde.");
        _status.Text = "Déconnexion des clés virtuelles pour figer les identifiants FIDO2…";
        await _sticks.DisconnectAllAsync();
        if (_sticks.GetConfiguredSticks().Any(stick => _sticks.IsConnected(stick.Name)))
            throw new IOException("Une clé USB virtuelle est encore active. Sauvegarde annulée.");
        _status.Text = "Chiffrement AES-256-GCM et vérification SHA-256 en cours…";
        var created = await Task.Run(() =>
        {
            lock (_vault.SyncRoot)
                return VaultBackupService.CreateBackup(VaultStore.DefaultVaultPath, destination, password);
        });
        _status.Text = "Sauvegarde créée et vérifiée : " + destination;
        await MessageWindow.ShowAsync(this, "Sauvegarde Nexora Vault terminée",
            "Fichier créé :\n" + destination + "\n\nDate : " + created.CreatedUtc.ToLocalTime().ToString("g") +
            "\n\nImportant : conservez aussi le PIN d'origine, le mot de passe de sauvegarde et le " +
            "code de récupération Nexora (indispensable sur un nouveau profil Windows). " +
            "Testez la restauration sur un environnement de confiance.");
        Close();
    }

    private async Task RestoreAsync(string password)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choisir une sauvegarde Nexora Vault",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Nexora Vault Backup") { Patterns = ["*.nxbackup"] }]
        });
        if (files.Count == 0) { _status.Text = "Restauration annulée."; return; }
        string source = files[0].TryGetLocalPath()
            ?? throw new IOException("La restauration nécessite un fichier local.");
        _status.Text = "Vérification du mot de passe et de l'intégrité de l'archive…";
        var info = await Task.Run(() => VaultBackupService.VerifyBackupPassword(source, password));
        if (!await ConfirmWindow.ShowAsync(this, "Remplacer le coffre Nexora ?",
            "Sauvegarde créée le " + info.CreatedUtc.ToLocalTime().ToString("g") + ".\n\n" +
            "Le coffre actif sera sauvegardé avant son remplacement. Les authentificateurs seront déconnectés " +
            "et Nexora se fermera. Sur un autre compte Windows, le code de récupération ORIGINAL et le PIN " +
            "sont indispensables pour déverrouiller les clés restaurées.\n\nContinuer ?", "Restaurer et fermer"))
        {
            _status.Text = "Restauration annulée. Aucun changement effectué.";
            return;
        }

        // Never replace a live FIDO2 vault: disconnect devices and dispose the master key first.
        _status.Text = "Déconnexion des clés FIDO2 et préparation d'une copie de secours…";
        await _sticks.DisconnectAllAsync();
        if (_sticks.GetConfiguredSticks().Any(stick => _sticks.IsConnected(stick.Name)))
            throw new IOException("Au moins une clé USB virtuelle est encore active. Restauration annulée.");
        _vault.Lock();
        string rollbackDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nexora", "Backups");
        var result = await Task.Run(() => VaultBackupService.RestoreBackup(source,
            VaultStore.DefaultVaultPath, password, rollbackDirectory));
        _status.Text = "Coffre restauré. Nexora doit redémarrer pour ouvrir la nouvelle session.";
        await MessageWindow.ShowAsync(this, "Nexora Vault restauré",
            "La restauration est terminée. Nexora va se fermer.\n\n" +
            (result.PreviousVaultCopy is null ? "Aucun ancien coffre n'était présent.\n\n"
                : "Ancien coffre conservé dans :\n" + result.PreviousVaultCopy + "\n\n") +
            "Pour rouvrir les clés, utilisez le PIN du coffre restauré. " +
            "Si ce profil Windows n'est pas autorisé, utilisez son code de récupération de 256 bits.");
        Close();
        await _main.ForceCloseAsync();
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
