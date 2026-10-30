using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Nexora.Gui.Dialogs;
using Nexora.Gui.Telemetry;
using Nexora.Gui.Vault;
using Nexora.Gui.Configuration;

namespace Nexora.Gui;

/// <summary>
/// UI-only navigation for the Nexora workspace. Keeps FIDO2, USB/IP and vault operations
/// delegated to the existing MainWindow and VaultBackupWindow implementations.
/// </summary>
public partial class MainWindow
{
    internal event Func<Task>? SettingsRequested;
    internal event Func<Task>? UpdatesRequested;
    private string _activeWorkspacePage = "dashboard";
    private bool _workspaceNavigationInitialized;

    internal void InitializeWorkspaceNavigation()
    {
        if (_workspaceNavigationInitialized) return;
        _workspaceNavigationInitialized = true;
        // The existing unlock path flips Dashboard.IsVisible and then refreshes the sticks.
        Dashboard.PropertyChanged += (_, args) =>
        {
            if (args.Property.Name != "IsVisible" || !Dashboard.IsVisible) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!_vault.IsUnlocked) return;
                SetWorkspacePage("dashboard");
            }, DispatcherPriority.Background);
        };
        SetWorkspacePage("dashboard");
    }

    private void GoDashboard_Click(object? sender, RoutedEventArgs e) => SetWorkspacePage("dashboard");
    private void GoKeys_Click(object? sender, RoutedEventArgs e) => SetWorkspacePage("keys");
    private void GoVault_Click(object? sender, RoutedEventArgs e) => SetWorkspacePage("vault");
    private void GoFortress_Click(object? sender, RoutedEventArgs e) => SetWorkspacePage("fortress");
    private void GoUsb_Click(object? sender, RoutedEventArgs e) => SetWorkspacePage("usb");
    private void GoDiagnostics_Click(object? sender, RoutedEventArgs e) => SetWorkspacePage("diagnostics");

    private async void SidebarSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (!_vault.IsUnlocked || SettingsRequested is null) return;
        try { await SettingsRequested(); }
        catch (Exception ex) { await MessageWindow.ShowAsync(this, "Paramètres", ex.Message); }
    }

    private async void SidebarUpdate_Click(object? sender, RoutedEventArgs e)
    {
        if (!_vault.IsUnlocked || UpdatesRequested is null) return;
        try { await UpdatesRequested(); }
        catch (Exception ex) { await MessageWindow.ShowAsync(this, "Mises à jour", ex.Message); }
    }

    private async void VaultBackup_Click(object? sender, RoutedEventArgs e)
    {
        if (!_vault.IsUnlocked) return;
        await VaultBackupWindow.ShowAsync(this, this, _stickManager, restore: false);
    }

    private async void VaultRestore_Click(object? sender, RoutedEventArgs e)
    {
        if (!_vault.IsUnlocked) return;
        await VaultBackupWindow.ShowAsync(this, this, _stickManager, restore: true);
    }

    private void RefreshWorkspace_Click(object? sender, RoutedEventArgs e)
    {
        if (!_vault.IsUnlocked) return;
        RefreshStickList();
        RefreshWorkspacePanels();
    }

    private void SetWorkspacePage(string page)
    {
        if (!_vault.IsUnlocked && Dashboard.IsVisible) return;
        _activeWorkspacePage = page;
        DashboardPage.IsVisible = page == "dashboard";
        KeysPage.IsVisible = page == "keys";
        VaultPage.IsVisible = page == "vault";
        FortressPage.IsVisible = page == "fortress";
        UsbPage.IsVisible = page == "usb";
        DiagnosticsPage.IsVisible = page == "diagnostics";

        foreach (var item in new (Button Button, string Page)[]
        {
            (NavDashboard, "dashboard"), (NavKeys, "keys"), (NavVault, "vault"),
            (NavFortress, "fortress"), (NavUsb, "usb"), (NavDiagnostics, "diagnostics")
        })
        {
            bool active = item.Page == page;
            item.Button.Background = Brush.Parse(active ? "#30234F" : "#0B1426");
            item.Button.BorderBrush = Brush.Parse(active ? "#58428B" : "#0B1426");
            item.Button.Foreground = Brush.Parse(active ? "#F0E5FF" : "#B6C6E2");
        }
        if (_vault.IsUnlocked)
            RefreshWorkspacePanels();
    }

    private void RefreshWorkspacePanels()
    {
        if (!_vault.IsUnlocked) return;
        var sticks = _stickManager.GetConfiguredSticks();
        int countConnected = sticks.Count(x => _stickManager.IsConnected(x.Name));
        UsbSystemStatusText.Text = countConnected + " connectée(s)";

        // Existing Add/Edit/Delete and Connect/Disconnect actions remain in BuildStickCard.
        // Build fresh cards here; each Avalonia control must have only one parent.
        StickListPanel.Children.Clear();
        if (sticks.Count == 0)
            StickListPanel.Children.Add(InfoCard("Aucune clé configurée", "Clique sur « Nouvelle clé » pour créer ton premier authentificateur."));
        else
            foreach (var stick in sticks)
                StickListPanel.Children.Add(BuildStickCard(stick));

        DashboardKeyPreviewPanel.Children.Clear();
        if (sticks.Count == 0)
            DashboardKeyPreviewPanel.Children.Add(InfoCard("Aucun authentificateur", "Aucune clé FIDO2 n'est encore configurée."));
        else
        {
            foreach (var stick in sticks.Take(3))
            {
                bool connected = _stickManager.IsConnected(stick.Name);
                var title = new TextBlock { Text = stick.Name, FontSize = 13, FontWeight = FontWeight.SemiBold };
                var detail = new TextBlock
                {
                    Text = connected ? "●  Connectée via USB/IP" : "○  Non connectée",
                    FontSize = 11, Foreground = Brush.Parse(connected ? "#69E4C2" : "#98AFCB")
                };
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
                var symbol = new TextBlock { Text = "◇", FontSize = 21, Foreground = Brush.Parse("#B199FF"), VerticalAlignment = VerticalAlignment.Center };
                var label = new StackPanel { Spacing = 4, Margin = new Thickness(12, 0), Children = { title, detail } };
                var manage = new Button { Content = "Gérer →", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                manage.Click += (_, _) => SetWorkspacePage("keys");
                Grid.SetColumn(label, 1); Grid.SetColumn(manage, 2);
                grid.Children.Add(symbol); grid.Children.Add(label); grid.Children.Add(manage);
                DashboardKeyPreviewPanel.Children.Add(new Border
                {
                    Background = Brush.Parse("#121F36"), CornerRadius = new CornerRadius(10),
                    BorderBrush = Brush.Parse("#2A3D5A"), BorderThickness = new Thickness(1),
                    Padding = new Thickness(13), Child = grid
                });
            }
            if (sticks.Count > 3)
                DashboardKeyPreviewPanel.Children.Add(new TextBlock
                {
                    Text = $"Et {sticks.Count - 3} autre(s) authentificateur(s)…",
                    FontSize = 11, Foreground = Brush.Parse("#98AFCB")
                });
        }

        UsbDevicesPanel.Children.Clear();
        if (sticks.Count == 0)
            UsbDevicesPanel.Children.Add(InfoCard("Aucun périphérique virtuel", "Crée une clé depuis l'onglet Authentificateurs."));
        else
        {
            foreach (var stick in sticks)
            {
                bool connected = _stickManager.IsConnected(stick.Name);
                var names = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                names.Children.Add(new TextBlock
                {
                    Text = "⌁  " + stick.Name, FontSize = 15, FontWeight = FontWeight.SemiBold,
                    Foreground = Brush.Parse("#EAF1FF")
                });
                names.Children.Add(new TextBlock
                {
                    Text = connected ? "●  Connectée" : "○  Déconnectée",
                    Foreground = Brush.Parse(connected ? "#6CE5C8" : "#A1AFC8"), FontSize = 11
                });
                var toggle = new Button { Content = connected ? "Déconnecter" : "Connecter", MinWidth = 113 };
                if (!connected) toggle.Classes.Add("accent");
                toggle.Click += async (_, _) =>
                {
                    toggle.IsEnabled = false;
                    try { await ToggleConnectAsync(stick, connected); }
                    catch (Exception ex) { await MessageWindow.ShowAsync(this, "USB/IP", ex.Message); }
                    finally { RefreshWorkspacePanels(); }
                };
                var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                Grid.SetColumn(toggle, 1); line.Children.Add(names); line.Children.Add(toggle);
                UsbDevicesPanel.Children.Add(new Border
                {
                    Child = line, Padding = new Thickness(17, 14), CornerRadius = new CornerRadius(11),
                    Background = Brush.Parse("#121F36"), BorderBrush = Brush.Parse("#2B3C55"), BorderThickness = new Thickness(1)
                });
            }
        }

        var events = SecurityAudit.Instance.Recent(60);
        DashboardActivityPanel.Children.Clear();
        DiagnosticsEventsPanel.Children.Clear();
        if (events.Count == 0)
        {
            DashboardActivityPanel.Children.Add(InfoLine("Aucune activité récente."));
            DiagnosticsEventsPanel.Children.Add(InfoCard("Aucun événement", "L'historique apparaîtra après les premières opérations."));
        }
        else
        {
            foreach (var entry in events.Take(4))
                DashboardActivityPanel.Children.Add(EventLine(entry, compact: true));
            foreach (var entry in events)
                DiagnosticsEventsPanel.Children.Add(EventLine(entry, compact: false));
        }
    }

    private static Control EventLine(SecurityAudit.Entry entry, bool compact)
    {
        string label = $"{entry.Time.ToLocalTime():HH:mm:ss}  ·  {entry.Category}  ·  {entry.Outcome}";
        var line = new TextBlock
        {
            Text = label,
            FontSize = compact ? 11 : 12, FontWeight = compact ? FontWeight.Normal : FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#C7D5F1")
        };
        if (compact) return line;
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(line);
        if (!string.IsNullOrWhiteSpace(entry.Description))
            stack.Children.Add(InfoLine(entry.Description));
        return new Border
        {
            Child = stack, Padding = new Thickness(12), CornerRadius = new CornerRadius(9),
            Background = Brush.Parse("#121F36"), BorderBrush = Brush.Parse("#2B3C55"),
            BorderThickness = new Thickness(1)
        };
    }

    private static TextBlock InfoLine(string message) => new()
    {
        Text = message, FontSize = 11, Foreground = Brush.Parse("#98ABC8"), TextWrapping = TextWrapping.Wrap
    };

    private static Border InfoCard(string title, string subtitle)
    {
        var stack = new StackPanel { Spacing = 7 };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold });
        stack.Children.Add(InfoLine(subtitle));
        return new Border
        {
            Child = stack, Background = Brush.Parse("#121F36"),
            BorderBrush = Brush.Parse("#2B3C55"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11), Padding = new Thickness(17)
        };
    }
}
