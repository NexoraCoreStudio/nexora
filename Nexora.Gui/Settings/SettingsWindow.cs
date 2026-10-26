using System.Diagnostics;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.Win32;
using Nexora.Crypt;
using Nexora.Gui.Dialogs;
using Nexora.Gui.DevTools;
using Nexora.Gui.Security;
using Nexora.Gui.Services;
using Nexora.Gui.Telemetry;
using Nexora.Gui.Updates;
using Nexora.Gui.Vault;

namespace Nexora.Gui.Settings;

/// <summary>
/// Local settings. The private DEV extension is deliberately not exposed here.
/// Reset makes a verified encrypted backup; uninstall never deletes user vault data.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private const string UninstallRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Nexora";
#if NEXORA_FORTRESS_PREVIEW
    private const bool FortressPreviewEnabled = true;
#else
    private const bool FortressPreviewEnabled = false;
#endif
    private readonly MainWindow _main;
    private readonly UpdateCoordinator _updates;
    private readonly IStickManager _sticks;
    private readonly VaultAccessController _vault = VaultAccessController.Instance;
    private readonly CheckBox _autoInstall;
    private readonly CheckBox _includePrereleases;
    private readonly CheckBox _startupCheck;
    private readonly CheckBox _hourlyCheck;
    private readonly TextBox _resetWord;
    private readonly TextBox _resetPin;
    private readonly TextBlock _status;
    private readonly Button _save;
    private readonly Button _resetVault;
    private readonly Button _activateFortress;
    private readonly TextBox _fortressPin;
    private bool _busy;

    public static Task ShowAsync(MainWindow main, UpdateCoordinator updates, IStickManager sticks) =>
        new SettingsWindow(main, updates, sticks).ShowDialog(main);

    private SettingsWindow(MainWindow main, UpdateCoordinator updates, IStickManager sticks)
    {
        _main = main;
        _updates = updates;
        _sticks = sticks;
        Title = "Nexora · Paramètres";
        Width = 640;
        Height = 715;
        MinWidth = 520;
        MinHeight = 570;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush.Parse("#0C1526");
        _autoInstall = Option("Installer automatiquement les mises à jour", updates.AutoInstall);
        _includePrereleases = Option("Recevoir aussi les préversions GitHub", updates.IncludePrereleases);
        _startupCheck = Option("Vérifier les mises à jour au lancement", updates.CheckOnStartup);
        _hourlyCheck = Option("Vérifier les mises à jour toutes les heures", updates.CheckEveryHour);
        _resetWord = new TextBox { Watermark = "Saisir REINITIALISER", MaxLength = 14 };
        _resetPin = new TextBox { Watermark = "Code PIN actuel (4 chiffres)", MaxLength = 4, PasswordChar = '●' };
        _status = new TextBlock
        {
            Foreground = Brush.Parse("#98DCEB"), TextWrapping = TextWrapping.Wrap,
            Text = "Les paramètres sont enregistrés uniquement sur ce PC."
        };
        _save = ActionButton("Enregistrer", "#244D85");
        _save.Click += Save_Click;
        var check = ActionButton("Vérifier maintenant", "#273A52");
        check.Click += async (_, _) =>
        {
            if (_busy) return;
            Close(); // Avoid opening a second modal over Settings.
            await _updates.CheckNowAsync();
        };
        var resetPreferences = ActionButton("Restaurer les paramètres", "#324056");
        resetPreferences.Click += ResetPreferences_Click;
        _resetVault = ActionButton("Réinitialiser Nexora", "#80404E");
        _resetVault.IsEnabled = _vault.IsConfigured && _vault.IsUnlocked;
        _resetVault.Click += ResetVault_Click;
        var uninstall = ActionButton("Désinstaller Nexora", "#7A3443");
        uninstall.Click += Uninstall_Click;
        var close = ActionButton("Fermer", "#273A52");
        close.Click += (_, _) => Close();

        var stack = new StackPanel { Spacing = 13, Margin = new Thickness(24, 20) };
        stack.Children.Add(new TextBlock
        {
            Text = "PARAMÈTRES", FontSize = 23, FontWeight = FontWeight.Bold,
            Foreground = Brush.Parse("#E7EFFF")
        });
        stack.Children.Add(new TextBlock
        {
            Text = "Nexora " + NexoraTitleBar.DisplayVersion + " · préférences locales et maintenance",
            Foreground = Brush.Parse("#99AFCB"), FontSize = 12
        });
        stack.Children.Add(Heading("MISES À JOUR"));
        stack.Children.Add(_startupCheck);
        stack.Children.Add(_hourlyCheck);
        stack.Children.Add(_autoInstall);
        stack.Children.Add(_includePrereleases);
        stack.Children.Add(new TextBlock
        {
            Text = "Les préversions peuvent être instables. Elles viennent uniquement des Releases du dépôt public Nexora. " +
                   "Le choix n'active pas le mode console DEV privé.",
            Foreground = Brush.Parse("#A4B6D1"), TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        });
        var updatesActions = Buttons(_save, check, resetPreferences);
        stack.Children.Add(updatesActions);
        stack.Children.Add(new Border { Height = 1, Background = Brush.Parse("#2A3B55"), Margin = new Thickness(0, 7) });
        stack.Children.Add(Heading("NEXORA VAULT · SAUVEGARDE ET RESTAURATION"));
        stack.Children.Add(new TextBlock
        {
            Text = "Exportez votre coffre dans un fichier .nxbackup chiffré par un mot de passe indépendant. " +
                   "Pour restaurer sur un autre compte Windows, conservez également le code de récupération d'origine " +
                   "et votre PIN. La restauration préserve l'ancien coffre avant le remplacement.",
            Foreground = Brush.Parse("#B8C7DD"), TextWrapping = TextWrapping.Wrap, FontSize = 12
        });
        var exportVault = ActionButton("Sauvegarder le coffre", "#63459D");
        exportVault.IsEnabled = _vault.IsConfigured && _vault.IsUnlocked;
        exportVault.Click += async (_, _) =>
        {
            if (_busy) return;
            await VaultBackupWindow.ShowAsync(this, _main, _sticks, restore: false);
        };
        var importVault = ActionButton("Restaurer une sauvegarde", "#315579");
        importVault.Click += async (_, _) =>
        {
            if (_busy) return;
            await VaultBackupWindow.ShowAsync(this, _main, _sticks, restore: true);
        };
        stack.Children.Add(Buttons(exportVault, importVault));
        stack.Children.Add(new Border { Height = 1, Background = Brush.Parse("#2A3B55"), Margin = new Thickness(0, 7) });
        stack.Children.Add(Heading("NEXORA CRYPT FORTRESS · APERÇU V2"));
        stack.Children.Add(new TextBlock
        {
            Text = "Migration volontaire et expérimentale du coffre : chaque élément obtient sa propre clé AES-256-GCM, " +
                   "protégée par une clé de chiffrement distincte (HKDF-SHA-256). Le manifeste reste chiffré. " +
                   "Une copie intégrale du coffre v1 est conservée avant remplacement. " +
                   "Cette première étape n'implémente PAS encore le TPM, Argon2id, ni un service isolé.",
            Foreground = Brush.Parse("#B8C7DD"), TextWrapping = TextWrapping.Wrap, FontSize = 12
        });
        _fortressPin = new TextBox
        {
            Watermark = "Confirmer le PIN Nexora actuel (4 chiffres)", MaxLength = 4, PasswordChar = '●',
            IsVisible = FortressPreviewEnabled
        };
        _activateFortress = ActionButton("Activer Fortress v2 (migration expérimentale)", "#604692");
        _activateFortress.IsEnabled = FortressPreviewEnabled && _vault.IsConfigured && _vault.IsUnlocked &&
                                     VaultStore.ReadVaultFormatVersion(VaultStore.DefaultVaultPath) == 1;
        if (!FortressPreviewEnabled)
            _activateFortress.Content = "Fortress v2 — apercu uniquement (build de test requis)";
        _activateFortress.Click += MigrateFortress_Click;
        stack.Children.Add(_fortressPin);
        stack.Children.Add(_activateFortress);
        stack.Children.Add(new TextBlock
        {
            Text = "N'activez pas cette migration sur votre coffre principal avant validation des tests Windows. " +
                   "Conservez aussi votre code de récupération de 256 bits et une sauvegarde .nxbackup indépendante.",
            Foreground = Brush.Parse("#F1C3AE"), TextWrapping = TextWrapping.Wrap, FontSize = 12
        });
        stack.Children.Add(new Border { Height = 1, Background = Brush.Parse("#2A3B55"), Margin = new Thickness(0, 7) });
        stack.Children.Add(Heading("RÉINITIALISATION"));
        stack.Children.Add(new TextBlock
        {
            Text = "Les préférences peuvent être restaurées ci-dessus. La réinitialisation complète déconnecte les clés et " +
                   "archive le coffre chiffré dans AppData\\Local\\Nexora\\Backups, sans détruire la sauvegarde.",
            Foreground = Brush.Parse("#B8C7DD"), TextWrapping = TextWrapping.Wrap, FontSize = 12
        });
        stack.Children.Add(_resetWord);
        stack.Children.Add(_resetPin);
        stack.Children.Add(_resetVault);
        stack.Children.Add(new Border { Height = 1, Background = Brush.Parse("#2A3B55"), Margin = new Thickness(0, 7) });
        stack.Children.Add(Heading("DÉSINSTALLATION"));
        stack.Children.Add(new TextBlock
        {
            Text = "Ouvre le programme de désinstallation officiel de Windows. " +
                   "Les clés FIDO2 et le coffre sont conservés, conformément au comportement de l'installateur.",
            Foreground = Brush.Parse("#B8C7DD"), TextWrapping = TextWrapping.Wrap, FontSize = 12
        });
        stack.Children.Add(uninstall);
        stack.Children.Add(_status);
        stack.Children.Add(close);
        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    }

    private static CheckBox Option(string content, bool checkedValue) => new()
    {
        Content = content, IsChecked = checkedValue,
        Foreground = Brush.Parse("#DFE9F8"), FontSize = 13
    };

    private static TextBlock Heading(string text) => new()
    {
        Text = text, FontSize = 12, FontWeight = FontWeight.Bold,
        Foreground = Brush.Parse("#C59DFF"), Margin = new Thickness(0, 6, 0, 0)
    };

    private static Button ActionButton(string content, string background) => new()
    {
        Content = content, Padding = new Thickness(12, 8),
        Background = Brush.Parse(background), Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Left
    };

    private static StackPanel Buttons(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var button in buttons) panel.Children.Add(button);
        return panel;
    }

    private async void Save_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        _save.IsEnabled = false;
        try
        {
            await _updates.SaveSettingsAsync(
                _autoInstall.IsChecked == true,
                _includePrereleases.IsChecked == true,
                _startupCheck.IsChecked == true,
                _hourlyCheck.IsChecked == true);
            _status.Text = "Préférences enregistrées.";
        }
        catch (Exception ex) { _status.Text = "Enregistrement impossible : " + ex.Message; }
        finally { _busy = false; _save.IsEnabled = true; }
    }

    private async void ResetPreferences_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_busy || !await ConfirmWindow.ShowAsync(this, "Restaurer les préférences",
            "Remettre les options de mise à jour aux valeurs par défaut ? Vos clés FIDO2 ne seront pas touchées.",
            "Restaurer")) return;
        _busy = true;
        try
        {
            await _updates.RestoreDefaultSettingsAsync();
            _autoInstall.IsChecked = false;
            _includePrereleases.IsChecked = false;
            _startupCheck.IsChecked = true;
            _hourlyCheck.IsChecked = true;
            _status.Text = "Préférences restaurées.";
        }
        catch (Exception ex) { _status.Text = "Échec : " + ex.Message; }
        finally { _busy = false; }
    }

    private async void MigrateFortress_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_busy || !FortressPreviewEnabled) return;
        string pin = _fortressPin.Text ?? string.Empty;
        _fortressPin.Text = string.Empty;
        if (!_vault.IsUnlocked || !_vault.IsConfigured)
        {
            _status.Text = "Deverrouillez Nexora Vault avant de migrer.";
            return;
        }
        if (!_vault.VerifyApproval(pin, out var error))
        {
            _status.Text = error;
            return;
        }
        if (!await ConfirmWindow.ShowAsync(this, "Nexora Crypt Fortress v2 — aperçu",
            "ATTENTION : migration expérimentale et NON AUDITEE. " +
            "Une copie complète du coffre v1 sera conservée, puis les éléments seront rechiffrés avec des clés individuelles. " +
            "Déconnectez vos clés USB virtuelles. " +
            "Cette opération ne peut pas être annulée sans restauration manuelle de la copie v1. " +
            "Avez-vous sauvegardé votre code de récupération et une archive .nxbackup ?",
            "Migrer le coffre (aperçu)")) return;

        _busy = true;
        _activateFortress.IsEnabled = false;
        try
        {
            await _sticks.DisconnectAllAsync();
            if (_sticks.GetConfiguredSticks().Any(key => _sticks.IsConnected(key.Name)))
                throw new IOException("Une clé FIDO2 est encore connectée. Migration annulée.");
            FortressMigrationResult result;
            string backups = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Nexora", "Backups", "Fortress");
            lock (_vault.SyncRoot)
                result = VaultFortressMigration.Migrate(_vault.Session, backups);
            _status.Text = "Fortress v2 active : " + result.MigratedItems + " elements conserves.";
            await MessageWindow.ShowAsync(this, "Migration Fortress v2 terminee",
                "Les cles FIDO2 ont ete migrees. Copie complete du coffre v1 :\n" + result.PreservedV1Vault +
                "\n\nNexora va maintenant fermer pour recreer proprement ses sessions FIDO2. " +
                "Conservez la sauvegarde v1 et votre code de recuperation.");
            Close();
            await _main.ForceCloseAsync();
            (Avalonia.Application.Current?.ApplicationLifetime as
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
        catch (Exception ex)
        {
            _status.Text = "Migration interrompue : " + ex.Message;
            _activateFortress.IsEnabled = FortressPreviewEnabled && _vault.IsConfigured && _vault.IsUnlocked &&
                                         VaultStore.ReadVaultFormatVersion(VaultStore.DefaultVaultPath) == 1;
        }
        finally { _busy = false; }
    }

    private async void ResetVault_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_busy) return;
        if (!string.Equals(_resetWord.Text?.Trim(), "REINITIALISER", StringComparison.Ordinal))
        {
            _status.Text = "Pour confirmer, écrivez REINITIALISER en majuscules.";
            return;
        }
        string enteredPin = _resetPin.Text ?? "";
        _resetPin.Text = string.Empty;
        if (!_vault.VerifyApproval(enteredPin, out var error))
        {
            _status.Text = error;
            return;
        }
        if (!await ConfirmWindow.ShowAsync(this, "Réinitialiser Nexora",
            "Toutes les clés configurées disparaîtront de la nouvelle installation. " +
            "Le coffre chiffré actuel sera archivé dans AppData\\Local\\Nexora\\Backups. " +
            "Conservez votre code PIN et votre code de récupération. Continuer ?", "Archiver et réinitialiser")) return;

        _busy = true;
        _resetVault.IsEnabled = false;
        try
        {
            // Do not archive the vault while an authenticator could still use it.
            await _sticks.DisconnectAllAsync();
            if (_sticks.GetConfiguredSticks().Any(key => _sticks.IsConnected(key.Name)))
                throw new IOException("Une clé FIDO2 est encore connectée. Réinitialisation annulée.");
            SecurityAudit.Instance.Record("APP", "reset-with-backup");
            _vault.Lock();
            string backup = BackupAndDetachVault();
            _status.Text = "Coffre sauvegardé : " + backup;
            await MessageWindow.ShowAsync(this, "Réinitialisation terminée",
                "Votre coffre chiffré a été sauvegardé à :\n" + backup +
                "\n\nRelancez Nexora pour créer une nouvelle configuration.");
            Close();
            await _main.ForceCloseAsync();
            (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
        catch (Exception ex)
        {
            _status.Text = "Réinitialisation interrompue : " + ex.Message;
            _resetVault.IsEnabled = _vault.IsConfigured && _vault.IsUnlocked;
        }
        finally { _busy = false; }
    }

    private static string BackupAndDetachVault()
    {
        string original = Path.GetFullPath(VaultStore.DefaultVaultPath);
        if (!File.Exists(original)) throw new FileNotFoundException("Coffre Nexora introuvable.", original);
        if ((File.GetAttributes(original) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Un coffre redirigé par un lien symbolique ne peut pas être réinitialisé.");
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nexora", "Backups");
        Directory.CreateDirectory(directory);
        string backup = Path.Combine(directory,
            "Nexora-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" +
            Guid.NewGuid().ToString("N")[..8] + ".vault");
        string temporary = backup + ".tmp";
        try
        {
            File.Copy(original, temporary, overwrite: false);
            byte[] originalHash;
            byte[] backupHash;
            using (var source = File.OpenRead(original)) originalHash = SHA256.HashData(source);
            using (var destination = File.OpenRead(temporary)) backupHash = SHA256.HashData(destination);
            if (!CryptographicOperations.FixedTimeEquals(originalHash, backupHash))
                throw new IOException("La sauvegarde du coffre n'a pas passé le contrôle d'intégrité.");
            File.Move(temporary, backup);
            // Deletion only after a complete, checksum-verified, encrypted backup.
            File.Delete(original);
            return backup;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async void Uninstall_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows requis.");
            using var key = Registry.CurrentUser.OpenSubKey(UninstallRegistryKey);
            var installed = key?.GetValue("InstallLocation") as string;
            string? running = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(installed) || string.IsNullOrWhiteSpace(running) ||
                !Path.GetFullPath(Path.Combine(installed, "Nexora.exe"))
                    .Equals(Path.GetFullPath(running), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Désinstallation réservée à Nexora installé par le Setup officiel. " +
                                      "Une compilation de développement n'est pas une installation Windows.");
            string setup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NexoraInstaller", "Nexora-Setup.exe");
            if (!File.Exists(setup))
                throw new FileNotFoundException("Programme de désinstallation introuvable.", setup);
            if (!await ConfirmWindow.ShowAsync(this, "Désinstaller Nexora",
                "Désinstaller Nexora de ce PC ? Vos données personnelles et Nexora.vault sont conservés. " +
                "Le pilote USB/IP n'est pas supprimé.", "Ouvrir le désinstallateur")) return;
            _busy = true;
            // The installer waits for this exact Nexora process to exit before showing its wizard.
            var start = new ProcessStartInfo(setup)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(setup)!
            };
            start.ArgumentList.Add("--uninstall");
            start.ArgumentList.Add("--wait-parent-pid=" + Environment.ProcessId);
            using var process = Process.Start(start)
                ?? throw new IOException("Impossible de lancer le désinstallateur.");
            Close();
            await _main.ForceCloseAsync();
            (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
        catch (Exception ex) { _busy = false; _status.Text = "Désinstallation impossible : " + ex.Message; }
    }
}
