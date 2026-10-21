using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Nexora.Crypt;
using Avalonia.Media;
using Nexora.Gui.Configuration;
using Nexora.Gui.Dialogs;
using Nexora.Gui.Security;
using Nexora.Gui.Telemetry;
using Nexora.Gui.Services;

namespace Nexora.Gui;

public partial class MainWindow : Window
{
    private readonly IStickManager _stickManager;
    private readonly IStickConfigStore _configStore;
    private readonly VaultAccessController _vault;
    private bool _allowClose;
    private bool _unlockInProgress;

    public MainWindow(IStickManager stickManager, IStickConfigStore configStore, VaultAccessController vault)
    {
        _stickManager = stickManager;
        _configStore = configStore;
        _vault = vault;
        _vault.AccessRevoked += OnVaultAccessRevoked;
        InitializeComponent();
        Opened += (_, _) => { PrepareLock(); PinEntry.Focus(); };
        Closing += MainWindow_Closing;
    }

    private void PrepareLock()
    {
        LockOverlay.IsVisible = true;
        Dashboard.IsVisible = false;
        ChangePinOverlay.IsVisible = false;
        PinEntry.Text = "";
        PinConfirm.Text = "";
        PinError.IsVisible = false;
        bool setup = !_vault.IsConfigured;
        LockTitle.Text = setup ? "Créer ton code PIN" : "Déverrouiller Nexora";
        LockInfo.Text = setup ? "Choisis un code PIN personnel à 4 chiffres" : "Entre ton code de 4 chiffres";
        UnlockButton.Content = setup ? "Créer le PIN" : "Déverrouiller";
        PinConfirm.IsVisible = setup;
        RecoveryCodeEntry.Text = "";
        RecoveryCodeEntry.IsVisible = false;
        if (!setup)
        {
            try
            {
                bool authorized = _vault.IsAuthorizedOnThisWindowsProfile;
                RecoveryCodeEntry.IsVisible = !authorized;
                if (!authorized)
                    LockInfo.Text = "Nouveau profil Windows : PIN + code de récupération requis";
            }
            catch (Exception ex)
            {
                // Do not incorrectly treat a damaged vault as an unregistered PC.
                LockInfo.Text = "Vérification du coffre impossible";
                PinError.Text = ex.Message;
                PinError.IsVisible = true;
            }
        }
    }

    private async void Lock_Click(object? sender, RoutedEventArgs e) => await LockAndDetachAsync();

    private async void PinEntry_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await SubmitPinAsync();
    }

    private async void Unlock_Click(object? sender, RoutedEventArgs e) => await SubmitPinAsync();

    private async Task SubmitPinAsync()
    {
        if (_unlockInProgress) return;
        _unlockInProgress = true;
        var entered = PinEntry.Text ?? "";
        bool firstSetup = !_vault.IsConfigured;
        string recovery = RecoveryCodeEntry.Text?.Trim() ?? "";
        UnlockButton.IsEnabled = false;
        try
        {
            if (firstSetup)
            {
                if (entered != (PinConfirm.Text ?? "")) { DisplayPinError("Les deux codes sont differents."); return; }
                // A pre-existing 1.1 directory is never overwritten or deleted by the 1.2 vault.
                if (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "Nexora")))
                    await MessageWindow.ShowAsync(this, "Migration des anciennes cles",
                        "Des cles 1.1 ont ete detectees. Elles ne sont PAS importees automatiquement. " +
                        "Gardez l'ancien dossier et ses mots de passe : le nouveau coffre commencera vide. " +
                        "Ne supprimez pas les anciennes donnees avant une migration verifiee.");
                VaultCreationResult created = _vault.Create(entered);
                await RecoveryCodeWindow.ShowAsync(this, created.RecoveryCode);
            }
            else
            {
                _vault.Open(entered, recovery);
                if (recovery.Length > 0)
                {
                    bool enroll = await ConfirmWindow.ShowAsync(this, "Autoriser ce profil Windows ?",
                        "Souhaitez-vous autoriser explicitement ce profil Windows a ouvrir ce coffre sans code de recuperation ?");
                    if (enroll) _vault.Session.AuthorizeThisComputer();
                }
            }
        }
        catch (InvalidVaultPinException ex) { DisplayPinError(ex.Message); return; }
        catch (VaultLockedException ex) { DisplayPinError(ex.Message); return; }
        catch (UnauthorizedAccessException ex)
        {
            if (!_vault.IsUnlocked && !firstSetup)
            {
                try
                {
                    if (!_vault.IsAuthorizedOnThisWindowsProfile)
                    {
                        RecoveryCodeEntry.IsVisible = true;
                        LockInfo.Text = "Nouveau profil Windows : PIN + code de récupération requis";
                    }
                }
                catch { /* Preserve original error below for invalid vault metadata. */ }
            }
            DisplayPinError(ex.Message);
            return;
        }
        catch (Exception ex) { if (_vault.IsUnlocked) _vault.Lock(); DisplayPinError(ex.Message); return; }
        finally { UnlockButton.IsEnabled = true; _unlockInProgress = false; }

        PinEntry.Text = "";
        PinConfirm.Text = "";
        RecoveryCodeEntry.Text = "";
        LockOverlay.IsVisible = false;
        Dashboard.IsVisible = true;
        SecurityAudit.Instance.Record("APP", "unlocked");
        RefreshStickList();
    }

    private void DisplayPinError(string error)
    {
        PinError.Text = error;
        PinError.IsVisible = true;
        PinEntry.Text = "";
        PinEntry.Focus();
    }

    private async void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        try { await LockAndDetachAsync(); }
        finally { Hide(); }
    }

    private void OnVaultAccessRevoked() => Dispatcher.UIThread.Post(async () =>
    {
        try { await LockAndDetachAsync(); }
        catch (Exception ex) { DisplayPinError("Echec de deconnexion USB/IP : " + ex.Message); }
    });

    private async Task LockAndDetachAsync()
    {
        // Keep vault open until every USB/IP authenticator is disconnected.
        Exception? detachError = null;
        try { await _stickManager.DisconnectAllAsync(); }
        catch (Exception ex) { detachError = ex; }
        finally
        {
            _vault.Lock();
            PrepareLock();
        }
        if (detachError is not null)
            DisplayPinError("Le coffre est verrouille, mais la deconnexion USB/IP doit etre verifiee : " + detachError.Message);
    }

    public async Task ForceCloseAsync()
    {
        await LockAndDetachAsync();
        _allowClose = true;
        Close();
    }

    private void RefreshStickList()
    {
        StickListPanel.Children.Clear();
        var sticks = _stickManager.GetConfiguredSticks();
        TotalSticksText.Text = sticks.Count.ToString();
        ConnectedSticksText.Text = sticks.Count(s => _stickManager.IsConnected(s.Name)).ToString();
        if (sticks.Count == 0)
        {
            StickListPanel.Children.Add(new Border
            {
                Background = Brush.Parse("#152238"), CornerRadius = new CornerRadius(14),
                Padding = new Thickness(26),
                Child = new TextBlock
                {
                    Text = "Aucune clé configurée. Clique sur « Ajouter une clé » pour commencer.",
                    Foreground = Brush.Parse("#A8BCD8"), TextWrapping = TextWrapping.Wrap
                }
            });
            UpdateAuditPreview();
            return;
        }
        foreach (var stick in sticks) StickListPanel.Children.Add(BuildStickCard(stick));
        UpdateAuditPreview();
    }

    private Control BuildStickCard(StickConfig stick)
    {
        bool connected = _stickManager.IsConnected(stick.Name);
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        header.Children.Add(new TextBlock
        {
            Text = connected ? "●" : "○", FontSize = 18,
            Foreground = Brush.Parse(connected ? "#45E0BE" : "#8598B2"),
            VerticalAlignment = VerticalAlignment.Center
        });
        var names = new StackPanel { Spacing = 5 };
        names.Children.Add(new TextBlock { Text = stick.Name, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White });
        names.Children.Add(new TextBlock
        {
            Text = $"{stick.SerialNumberIdentifier}  •  {(connected ? "Connectée" : "Déconnectée")}",
            FontSize = 12, Foreground = Brush.Parse("#9BB0CC")
        });
        header.Children.Add(names);

        var edit = new Button { Content = "Modifier", Margin = new Thickness(0, 0, 5, 0) };
        edit.Click += async (_, _) => await EditStickAsync(stick);
        var credentials = new Button { Content = "Identifiants", IsEnabled = connected, Margin = new Thickness(0, 0, 5, 0) };
        credentials.Click += async (_, _) => await CredentialsWindow.ShowAsync(this, _stickManager, stick);
        var toggle = new Button { Content = connected ? "Déconnecter" : "Connecter", MinWidth = 110 };
        if (!connected) toggle.Classes.Add("accent");
        toggle.Click += async (_, _) => await ToggleConnectAsync(stick, connected);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(edit);
        actions.Children.Add(credentials);
        actions.Children.Add(toggle);

        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        actions.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(header, 0); Grid.SetColumn(actions, 1);
        layout.Children.Add(header); layout.Children.Add(actions);
        return new Border
        {
            Child = layout, Background = Brush.Parse("#17263C"),
            BorderBrush = Brush.Parse("#2B3E59"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(18)
        };
    }

    private async void AddStick_Click(object? sender, RoutedEventArgs e) => await AddStickAsync();

    private async Task AddStickAsync()
    {
        if (!_vault.IsUnlocked) return; // No key management from the tray without the PIN.
        var created = await AddStickWindow.ShowAsync(this, _configStore);
        if (created != null) RefreshStickList();
    }

    /// <summary>Show the app even when it is minimized to the notification area.</summary>
    public void OpenFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (!_vault.IsUnlocked) PinEntry.Focus();
    }

    public void ManageKeysFromTray()
    {
        OpenFromTray();
        if (_vault.IsUnlocked) RefreshStickList();
    }

    public async Task AddKeyFromTrayAsync()
    {
        OpenFromTray();
        await AddStickAsync(); // If locked, only the normal PIN screen is shown.
    }

    public void RefreshFromTray()
    {
        OpenFromTray();
        if (_vault.IsUnlocked) RefreshStickList();
    }

    public async Task HistoryFromTrayAsync()
    {
        OpenFromTray();
        if (!_vault.IsUnlocked) return;
        var events = SecurityAudit.Instance.Recent(40);
        var text = events.Count == 0 ? "Aucun événement." : string.Join(Environment.NewLine,
            events.Select(x => $"{x.Time.ToLocalTime():dd/MM HH:mm:ss}  {x.Category}  {x.Outcome}  {x.Description}"));
        await MessageWindow.ShowAsync(this, "Historique local Nexora", text);
    }

    public async Task LockFromTrayAsync()
    {
        await LockAndDetachAsync();
        OpenFromTray();
    }

    private async Task EditStickAsync(StickConfig stick)
    {
        var (edited, deleted) = await AddStickWindow.EditAsync(this, _configStore, _stickManager, stick);
        if (edited != null || deleted) RefreshStickList();
    }

    private async Task ToggleConnectAsync(StickConfig stick, bool wasConnected)
    {
        var result = wasConnected
            ? await _stickManager.DisconnectAsync(stick.Name)
            : await _stickManager.ConnectAsync(stick.Name);
        SecurityAudit.Instance.Record("USB", result.Outcome.ToString(), wasConnected ? "disconnect" : "connect");
        await ReportAttachResultAsync(result);
        RefreshStickList();
    }

    private async Task ReportAttachResultAsync(StickAttachResult result)
    {
        if (result.Outcome is StickAttachOutcome.UnsupportedPlatform or StickAttachOutcome.Failed)
            await MessageWindow.ShowAsync(this, "Nexora", result.Error ?? "Opération impossible.");
    }
    private void Refresh_Click(object? sender, RoutedEventArgs e) => RefreshStickList();

    private void UpdateAuditPreview()
    {
        var events = SecurityAudit.Instance.Recent(4);
        AuditPreviewText.Text = events.Count == 0 ? "Aucune activité." :
            string.Join("   ·   ", events.Select(x => $"{x.Time.ToLocalTime():HH:mm} {x.Category}: {x.Outcome}"));
    }

    private async void History_Click(object? sender, RoutedEventArgs e)
    {
        var events = SecurityAudit.Instance.Recent(40);
        var body = events.Count == 0 ? "Aucun événement." : string.Join(Environment.NewLine,
            events.Select(x => $"{x.Time.ToLocalTime():dd/MM HH:mm:ss}  {x.Category}  {x.Outcome}  {x.Description}"));
        await MessageWindow.ShowAsync(this, "Historique local Nexora", body);
    }

    private void ShowChangePin_Click(object? sender, RoutedEventArgs e)
    {
        ChangePinError.IsVisible = false;
        OldPinEntry.Text = NewPinEntry.Text = NewPinConfirmEntry.Text = string.Empty;
        ChangePinOverlay.IsVisible = true;
        OldPinEntry.Focus();
    }

    private void CancelChangePin_Click(object? sender, RoutedEventArgs e)
    {
        OldPinEntry.Text = NewPinEntry.Text = NewPinConfirmEntry.Text = string.Empty;
        ChangePinOverlay.IsVisible = false;
    }

    private void ChangePin_Click(object? sender, RoutedEventArgs e)
    {
        if (NewPinEntry.Text != NewPinConfirmEntry.Text)
        {
            ChangePinError.Text = "La confirmation ne correspond pas.";
            ChangePinError.IsVisible = true;
            return;
        }
        try
        {
            _vault.ChangePin(OldPinEntry.Text ?? "", NewPinEntry.Text ?? "");
        }
        catch (Exception ex)
        {
            SecurityAudit.Instance.Record("PIN", "change-denied");
            ChangePinError.Text = ex.Message;
            ChangePinError.IsVisible = true;
            return;
        }
        SecurityAudit.Instance.Record("PIN", "changed");
        CancelChangePin_Click(sender, e);
        UpdateAuditPreview();
    }

}
