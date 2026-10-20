using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
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
    private readonly LocalPinGuard _pin = LocalPinGuard.Instance;
    private bool _allowClose;

    public MainWindow(IStickManager stickManager, IStickConfigStore configStore)
    {
        _stickManager = stickManager;
        _configStore = configStore;
        InitializeComponent();
        Opened += (_, _) => { PrepareLock(); RefreshStickList(); PinEntry.Focus(); };
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
        bool setup = !_pin.IsConfigured;
        LockTitle.Text = setup ? "Créer ton code PIN" : "Déverrouiller Nexora";
        LockInfo.Text = setup ? "Choisis un code PIN personnel à 4 chiffres" : "Entre ton code de 4 chiffres";
        UnlockButton.Content = setup ? "Créer le PIN" : "Déverrouiller";
        PinConfirm.IsVisible = setup;
    }

    private void Lock_Click(object? sender, RoutedEventArgs e) { PrepareLock(); PinEntry.Focus(); }

    private void PinEntry_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SubmitPin();
    }

    private void Unlock_Click(object? sender, RoutedEventArgs e) => SubmitPin();

    private void SubmitPin()
    {
        var entered = PinEntry.Text ?? "";
        if (!_pin.IsConfigured)
        {
            if (entered != (PinConfirm.Text ?? "")) { DisplayPinError("Les deux codes sont différents."); return; }
            if (!_pin.TryInitialize(entered, out var err)) { DisplayPinError(err); return; }
        }
        else if (!_pin.Verify(entered, out var err)) { SecurityAudit.Instance.Record("PIN", "failed"); DisplayPinError(err); return; }

        PinEntry.Text = "";
        PinConfirm.Text = "";
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

    private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        PrepareLock(); // UI only: does NOT detach hardware. Approvals require PIN.
        SecurityAudit.Instance.Record("APP", "hidden");
        Hide();
    }

    public void ForceClose() { _allowClose = true; Close(); }

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

    private async void AddStick_Click(object? sender, RoutedEventArgs e)
    {
        var created = await AddStickWindow.ShowAsync(this, _configStore);
        if (created != null) RefreshStickList();
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
        if (!_pin.Change(OldPinEntry.Text ?? "", NewPinEntry.Text ?? "", out var error))
        {
            SecurityAudit.Instance.Record("PIN", "change-denied");
            ChangePinError.Text = error;
            ChangePinError.IsVisible = true;
            return;
        }
        SecurityAudit.Instance.Record("PIN", "changed");
        CancelChangePin_Click(sender, e);
        UpdateAuditPreview();
    }

}
