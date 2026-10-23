using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Nexora.Gui.Updates;

public partial class UpdateWindow : Window
{
    private readonly Func<UpdateWindow, Task> _install;
    private readonly Action<bool> _saveAuto;
    private readonly Action _dismiss;
    private bool _working;

    internal UpdateWindow(UpdateReleaseClient.Release release, bool automatic, bool canInstall,
        Func<UpdateWindow, Task> install, Action<bool> saveAuto, Action dismiss, bool startAutomatically)
    {
        InitializeComponent();
        _install = install;
        _saveAuto = saveAuto;
        _dismiss = dismiss;
        VersionLabel.Text = "Nexora " + release.Version + (release.IsPrerelease ? " · PRÉVERSION" : "");
        string notes = string.IsNullOrWhiteSpace(release.Notes)
            ? "La nouvelle version est prête à être installée."
            : release.Notes.Length > 1800 ? release.Notes[..1800] + "…" : release.Notes;
        ReleaseNotes.Text = (release.IsPrerelease
            ? "ATTENTION : version de test GitHub, potentiellement instable.\n\n" : "") + notes;
        AutomaticCheck.IsChecked = automatic;
        InstallButton.IsEnabled = canInstall;
        if (!canInstall)
            StatusLabel.Text = "Installation portable ou non reconnue : utilisez le Setup officiel pour activer les mises à jour automatiques.";
        if (startAutomatically && canInstall)
            Opened += async (_, _) => await BeginInstallAsync();
    }

    private void AutomaticChanged(object? sender, RoutedEventArgs e)
    {
        if (AutomaticCheck != null) _saveAuto(AutomaticCheck.IsChecked == true);
    }

    private void Later_Click(object? sender, RoutedEventArgs e)
    {
        if (_working) return;
        _dismiss();
        Close();
    }

    private async void Install_Click(object? sender, RoutedEventArgs e) => await BeginInstallAsync();

    private async Task BeginInstallAsync()
    {
        if (_working) return;
        _working = true;
        InstallButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        AutomaticCheck.IsEnabled = false;
        DownloadProgress.IsVisible = true;
        SetProgress(0);
        try
        {
            await _install(this);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Mise à jour interrompue : " + ex.Message;
            DownloadProgress.IsVisible = false;
            LaterButton.IsEnabled = true;
            InstallButton.IsEnabled = true;
            AutomaticCheck.IsEnabled = true;
        }
        finally { _working = false; }
    }

    internal void SetProgress(int percent)
    {
        DownloadProgress.Value = percent;
        StatusLabel.Text = $"Téléchargement et vérification : {percent} %";
    }

    internal void SetStatus(string message) => StatusLabel.Text = message;
}
