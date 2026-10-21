using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Nexora.Gui.Dialogs;

/// <summary>
/// Shows the one-time vault recovery secret as selectable text and offers an
/// explicit, user-initiated clipboard copy action. The code is never logged.
/// </summary>
public partial class RecoveryCodeWindow : Window
{
    private readonly string _recoveryCode;

    public RecoveryCodeWindow()
    {
        InitializeComponent();
        _recoveryCode = string.Empty;
        Closing += RecoveryCodeWindow_Closing;
    }

    private RecoveryCodeWindow(string recoveryCode) : this()
    {
        if (string.IsNullOrWhiteSpace(recoveryCode))
            throw new ArgumentException("Le code de récupération est vide.", nameof(recoveryCode));

        _recoveryCode = recoveryCode;
        RecoveryCodeBox.Text = recoveryCode;
        Opened += (_, _) => RecoveryCodeBox.Focus();
    }

    public static Task ShowAsync(Window owner, string recoveryCode) =>
        new RecoveryCodeWindow(recoveryCode).ShowDialog(owner);

    private async void CopyCode_Click(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            ShowStatus("Presse-papiers indisponible. Sélectionnez le code et utilisez Ctrl+C.");
            return;
        }

        try
        {
            await clipboard.SetTextAsync(_recoveryCode);
            ShowStatus("Code copié. Sauvegardez-le hors ligne puis effacez le presse-papiers.");
        }
        catch (Exception)
        {
            ShowStatus("Copie impossible. Sélectionnez le code et utilisez Ctrl+C.");
        }
    }

    private void Continue_Click(object? sender, RoutedEventArgs e)
    {
        if (SavedCodeCheckBox.IsChecked != true)
        {
            ShowStatus("Confirmez d'abord avoir sauvegardé le code hors ligne.");
            return;
        }

        Close();
    }

    private void RecoveryCodeWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (SavedCodeCheckBox.IsChecked == true)
            return;

        e.Cancel = true;
        ShowStatus("Ce code ne sera plus affiché. Sauvegardez-le puis cochez la confirmation avant de fermer.");
    }

    private void ShowStatus(string message)
    {
        CopyStatus.Text = message;
        CopyStatus.IsVisible = true;
    }
}
