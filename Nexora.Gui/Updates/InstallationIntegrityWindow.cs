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
        Width = 740;
        Height = 620;
        MinWidth = 570;
        MinHeight = 430;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush.Parse("#0C1526");
        var title = new TextBlock
        {
            Text = "VÉRIFICATION DE L'INTÉGRITÉ", FontSize = 21, FontWeight = FontWeight.Bold,
            Foreground = Brush.Parse("#E6F0FF")
        };
        var description = new TextBlock
        {
            Text = "Comparaison avec la Release GitHub de la même version. " +
                   "Cette opération ne modifie pas les fichiers et n'ouvre pas votre coffre chiffré.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#AFC3DC"), FontSize = 12
        };
        _summary = new TextBlock
        {
            Text = "Analyse en cours…", FontSize = 17, FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#87DEFC")
        };
        _progress = new TextBlock
        {
            Text = "Préparation du contrôle…", TextWrapping = TextWrapping.Wrap,
            Foreground = Brush.Parse("#BBCDE0"), FontSize = 12
        };
        _details = new TextBox
        {
            Text = "Connexion à GitHub et contrôle des empreintes SHA-256…",
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"), MinHeight = 260,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _close = new Button
        {
            Content = "Annuler", MinWidth = 120,
            Padding = new Thickness(15, 9),
            Background = Brush.Parse("#30465F"), Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        _close.Click += (_, _) => Close();
        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto"),
            Margin = new Thickness(22)
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
        At(_details, 4);
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
            _summary.Foreground = Brush.Parse(!result.ManifestVerified ? "#FFCF93" :
                result.Problems.Count > 0 || result.UnexpectedBinaries.Count > 0 ? "#FF9CA8" : "#83E5C3");
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
            _summary.Foreground = Brush.Parse("#FFCF93");
            _progress.Text = "Aucun résultat de conformité n'est disponible.";
            _details.Text = "Erreur pendant le contrôle : " + ex.Message;
        }
        finally { _close.Content = "Fermer"; }
    }
}
