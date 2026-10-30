using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Nexora.Gui.Updates;

/// <summary>Read-only verification dialog; it never repairs or overwrites an installation.</summary>
internal sealed class InstallationIntegrityWindow : Window
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly bool _includePrereleases;
    private readonly TextBlock _progress;
    private readonly TextBlock _summary;
    private readonly TextBox _details;
    private readonly Button _close;
    private bool _begun;

    public static Task ShowAsync(Window owner, bool includePrereleases) =>
        new InstallationIntegrityWindow(includePrereleases).ShowDialog(owner);

    private InstallationIntegrityWindow(bool includePrereleases)
    {
        _includePrereleases = includePrereleases;
        Title = "Nexora · Vérification des fichiers";
        Width = 795;
        Height = 685;
        MinWidth = 640;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush.Parse("#090D18");
        var title = new TextBlock
        {
            Text = "Vérification de l’installation", FontSize = 24, FontWeight = FontWeight.Bold,
            Foreground = Brush.Parse("#F3F5FF")
        };
        var description = new TextBlock
        {
            Text = "Comparaison avec la Release GitHub de la même version. " +
                   "Cette opération ne modifie pas les fichiers et n'ouvre pas votre coffre chiffré.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#A6BBD8"), FontSize = 12
        };
        _summary = new TextBlock
        {
            Text = "Analyse en cours…", FontSize = 17, FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#8AEAE1")
        };
        _progress = new TextBlock
        {
            Text = "Préparation du contrôle…", TextWrapping = TextWrapping.Wrap,
            Foreground = Brush.Parse("#AFC1DA"), FontSize = 12
        };
        _details = new TextBox
        {
            Background = Brush.Parse("#0D1729"),
            BorderBrush = Brush.Parse("#253650"),
            Foreground = Brush.Parse("#CEDAF0"),
            Text = "Connexion à GitHub et contrôle des empreintes SHA-256…",
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"), MinHeight = 260,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _close = new Button
        {
            Content = "Annuler", MinWidth = 120,
            Padding = new Thickness(15, 9),
            Background = Brush.Parse("#263951"), Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        _close.Click += (_, _) => Close();
        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto"),
            Margin = new Thickness(27)
        };
        title.Margin = new Thickness(0, 0, 0, 9);
        description.Margin = new Thickness(0, 0, 0, 21);
        _summary.Margin = new Thickness(0, 0, 0, 9);
        _progress.Margin = new Thickness(0, 0, 0, 14);
        _close.Margin = new Thickness(0, 17, 0, 0);
        var resultsCard = new Border
        {
            Background = Brush.Parse("#121D31"),
            BorderBrush = Brush.Parse("#2E3C59"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(11),
            Child = _details
        };
        void At(Control control, int row)
        {
            Grid.SetRow(control, row);
            layout.Children.Add(control);
        }
        At(title, 0);
        At(description, 1);
        At(_summary, 2);
        At(_progress, 3);
        At(resultsCard, 4);
        At(_close, 5);
        Content = layout;
        Opened += async (_, _) =>
        {
            if (_begun) return;
            _begun = true;
            await VerifyAsync();
        };
        Closed += (_, _) => _cancellation.Cancel();
    }

    private async Task VerifyAsync()
    {
        try
        {
            using var service = new InstallationIntegrityService();
            var progress = new Progress<string>(message =>
            {
                if (!_cancellation.IsCancellationRequested)
                    Dispatcher.UIThread.Post(() => _progress.Text = message);
            });
            var result = await Task.Run(
                () => service.VerifyAsync(_includePrereleases, progress, _cancellation.Token),
                _cancellation.Token);
            if (_cancellation.IsCancellationRequested) return;
            _summary.Text = result.Status;
            _summary.Foreground = Brush.Parse(!result.ManifestVerified ? "#FFD09A" :
                result.Problems.Count > 0 || result.UnexpectedBinaries.Count > 0 ? "#FF9DA9" : "#7DE8C5");
            _progress.Text = result.ManifestVerified
                ? $"{result.CheckedFiles}/{result.ExpectedFiles} fichiers vérifiés · {result.Problems.Count} anomalies · " +
                  $"{result.UnexpectedBinaries.Count} exécutables supplémentaires"
                : "Impossible de confirmer la conformité des fichiers.";
            _details.Text = result.ToDisplayText();
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            if (_cancellation.IsCancellationRequested) return;
            _summary.Text = "VÉRIFICATION IMPOSSIBLE";
            _summary.Foreground = Brush.Parse("#FFD09A");
            _progress.Text = "Aucun résultat de conformité n'est disponible.";
            _details.Text = "Erreur pendant le contrôle : " + ex.Message;
        }
        finally { _close.Content = "Fermer"; }
    }
}
