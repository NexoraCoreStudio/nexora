using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Nexora.Gui.Configuration;
using Nexora.Gui.Services;
using Nexora.SecretManager;

namespace Nexora.Gui.Dialogs;

/// <summary>
/// Read-only view of FIDO2 credential metadata. No private key material is loaded for this UI.
/// Visual overhaul only: credential access, deletion and confirmation are unchanged.
/// </summary>
public partial class CredentialsWindow : Window
{
    private IStickManager? _stickManager;
    private StickConfig? _stick;

    public CredentialsWindow() => InitializeComponent();

    public static async Task ShowAsync(Window owner, IStickManager stickManager, StickConfig stick)
    {
        var window = new CredentialsWindow
        {
            Title = $"Identifiants FIDO2 · {stick.Name}",
            _stickManager = stickManager,
            _stick = stick,
        };
        await window.RefreshAsync();
        await window.ShowDialog(owner);
    }

    private async Task RefreshAsync()
    {
        CredentialListPanel.Children.Clear();
        IReadOnlyList<CredentialInfo> credentials;
        try
        {
            credentials = await _stickManager!.GetCredentialsAsync(_stick!.Name);
        }
        catch (Exception ex)
        {
            SubtitleText.Text = $"Impossible de charger les identifiants : {ex.Message}";
            return;
        }
        SubtitleText.Text = credentials.Count == 0
            ? "Aucun identifiant enregistré sur cette clé."
            : $"{credentials.Count} identifiant(s) conservé(s) dans Nexora Vault.";
        foreach (var credential in credentials)
            CredentialListPanel.Children.Add(BuildCredentialCard(credential));
    }

    private Control BuildCredentialCard(CredentialInfo credential)
    {
        var infoPanel = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        infoPanel.Children.Add(new TextBlock
        {
            Text = credential.RpId,
            FontWeight = FontWeight.SemiBold,
            FontSize = 14,
            Foreground = Brush.Parse("#F1F4FF"),
            TextWrapping = TextWrapping.Wrap
        });
        infoPanel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(credential.UserDisplayName)
                ? credential.UserName
                : $"{credential.UserDisplayName} ({credential.UserName})",
            Foreground = Brush.Parse("#AEC0DE"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });
        infoPanel.Children.Add(new TextBlock
        {
            Text = $"{(credential.IsResident ? "Résident" : "Non résident")}  ·  signatures {credential.SignCount}  ·  credProtect {credential.CredProtect}",
            Foreground = Brush.Parse("#8499BA"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        });
        var deleteButton = new Button
        {
            Content = "Supprimer",
            Background = Brush.Parse("#392432"),
            Foreground = Brush.Parse("#FFB0C0"),
            BorderBrush = Brush.Parse("#684052"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        deleteButton.Click += async (_, _) => await DeleteCredentialAsync(credential);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(infoPanel);
        Grid.SetColumn(deleteButton, 1);
        grid.Children.Add(deleteButton);
        return new Border
        {
            Padding = new Thickness(17, 15),
            CornerRadius = new CornerRadius(12),
            BorderBrush = Brush.Parse("#2E3A57"),
            Background = Brush.Parse("#172239"),
            BorderThickness = new Thickness(1),
            Child = grid
        };
    }

    private async Task DeleteCredentialAsync(CredentialInfo credential)
    {
        var confirmed = await ConfirmWindow.ShowAsync(
            this,
            "Supprimer l'identifiant",
            $"Supprimer l'identifiant pour « {credential.RpId} » ({credential.UserName}) ? Cette opération est irréversible.");
        if (!confirmed) return;
        try
        {
            await _stickManager!.DeleteCredentialAsync(_stick!.Name, credential.CredentialId);
        }
        catch (Exception ex)
        {
            await MessageWindow.ShowAsync(this, "Nexora", $"Impossible de supprimer l'identifiant : {ex.Message}");
            return;
        }
        await RefreshAsync();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
