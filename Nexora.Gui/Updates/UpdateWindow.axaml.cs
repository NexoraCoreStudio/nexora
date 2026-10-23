using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace Nexora.Gui.Updates;

/// <summary>
/// Responsive, UI-only representation of an already validated GitHub Release.
/// Download verification, installation gating and vault shutdown remain owned by UpdateCoordinator.
/// </summary>
public partial class UpdateWindow : Window
{
    private const string ReleasesPage = "https://github.com/NexoraCoreStudio/nexora/releases/tag/";
    private static readonly Regex HeadingPattern = new(@"^\s*(#{1,6})\s+(.+?)\s*#*\s*$", RegexOptions.Compiled);
    private static readonly Regex BulletPattern = new(@"^\s*[-*+]\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex NumberedPattern = new(@"^\s*(\d{1,3})[.)]\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex LinkPattern = new(@"!?\[([^\]]+)\]\((?:https?://)[^)]*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly Func<UpdateWindow, Task> _install;
    private readonly Action<bool> _saveAuto;
    private readonly Action _dismiss;
    private readonly bool _canInstall;
    private readonly Uri _changelogUrl;
    private bool _working;

    internal UpdateWindow(UpdateReleaseClient.Release release, bool automatic, bool canInstall,
        Func<UpdateWindow, Task> install, Action<bool> saveAuto, Action dismiss, bool startAutomatically)
    {
        _install = install;
        _saveAuto = saveAuto;
        _dismiss = dismiss;
        _canInstall = canInstall;
        _changelogUrl = new Uri(ReleasesPage + Uri.EscapeDataString(release.Tag));

        InitializeComponent();

        // Values are taken from the installed assembly and the actual GitHub asset.
        string installedVersion = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "Inconnue";
        VersionLabel.Text = "Nexora " + release.Version.ToString(3);
        InstalledVersionLabel.Text = "v" + installedVersion;
        AvailableVersionLabel.Text = "v" + release.Version.ToString(3);
        PackageSizeLabel.Text = (release.Size / (1024d * 1024d)).ToString("0.0", CultureInfo.CurrentCulture) + " Mo";

        ReleaseChannelLabel.Text = release.IsPrerelease ? "PRÉVERSION" : "VERSION STABLE";
        ReleaseChannelLabel.Foreground = Brush.Parse(release.IsPrerelease ? "#FFC98A" : "#83E5DE");

        RenderNotes(release.Notes, release.IsPrerelease);
        AutomaticCheck.IsChecked = automatic;
        InstallButton.IsEnabled = canInstall;

        if (!canInstall)
        {
            StageLabel.Text = "MODE PORTABLE / DEV";
            StatusLabel.Text = "Installation automatique indisponible : lance le Setup officiel depuis GitHub.";
        }
        else if (startAutomatically)
        {
            Opened += async (_, _) => await BeginInstallAsync();
        }
    }

    private void AutomaticChanged(object? sender, RoutedEventArgs e)
    {
        if (AutomaticCheck is not null && !_working)
            _saveAuto(AutomaticCheck.IsChecked == true);
    }

    private void Later_Click(object? sender, RoutedEventArgs e)
    {
        if (_working) return;
        _dismiss();
        Close();
    }

    private void Changelog_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_changelogUrl.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            SetStatus("Impossible d'ouvrir le navigateur. Notes disponibles sur GitHub → Nexora → Releases.");
        }
    }

    private async void Install_Click(object? sender, RoutedEventArgs e) => await BeginInstallAsync();

    private async Task BeginInstallAsync()
    {
        if (_working || !_canInstall) return;
        _working = true;
        InstallButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        AutomaticCheck.IsEnabled = false;
        DownloadProgress.IsVisible = true;
        SetProgress(0);
        try
        {
            // Existing verified download, USB/IP safety checks and installer handoff.
            await _install(this);
        }
        catch (Exception ex)
        {
            StageLabel.Text = "ÉCHEC";
            StatusLabel.Text = "Mise à jour interrompue : " + ex.Message;
            DownloadProgress.IsVisible = false;
            LaterButton.IsEnabled = true;
            InstallButton.IsEnabled = _canInstall;
            AutomaticCheck.IsEnabled = true;
        }
        finally { _working = false; }
    }

    /// <summary>0–90: download; 90–100: SHA-256 verification/extraction.</summary>
    internal void SetProgress(int percent)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetProgress(percent));
            return;
        }
        percent = Math.Clamp(percent, 0, 100);
        DownloadProgress.IsVisible = true;
        DownloadProgress.Value = percent;
        if (percent < 90)
        {
            StageLabel.Text = "TÉLÉCHARGEMENT";
            StatusLabel.Text = $"Réception du package : {percent} % du processus";
        }
        else if (percent < 100)
        {
            StageLabel.Text = "VÉRIFICATION";
            StatusLabel.Text = $"Contrôle SHA-256 et extraction : {percent} %";
        }
        else
        {
            StageLabel.Text = "VÉRIFIÉ";
            StatusLabel.Text = "Package téléchargé et vérifié avec SHA-256.";
        }
    }

    internal void SetStatus(string message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetStatus(message));
            return;
        }
        if (message.Contains("Fermeture", StringComparison.OrdinalIgnoreCase)
            || message.Contains("installateur", StringComparison.OrdinalIgnoreCase))
            StageLabel.Text = "INSTALLATION";
        else if (message.Contains("Déconnectez", StringComparison.OrdinalIgnoreCase))
            StageLabel.Text = "ACTION REQUISE";
        StatusLabel.Text = message;
    }

    private void RenderNotes(string notes, bool prerelease)
    {
        ReleaseNotesPanel.Children.Clear();
        if (prerelease)
        {
            var preNotice = CreateText(
                "Cette version est une préversion : certaines fonctionnalités peuvent encore évoluer.",
                12, "#FFD29A", FontWeight.SemiBold);
            preNotice.Margin = new Thickness(0, 0, 0, 8);
            ReleaseNotesPanel.Children.Add(preNotice);
        }

        if (string.IsNullOrWhiteSpace(notes))
        {
            ReleaseNotesPanel.Children.Add(CreateText("Aucune note de version détaillée n'est disponible.", 12, "#C7D7E9"));
            return;
        }

        // Render only a bounded, plaintext Markdown subset. GitHub content is untrusted:
        // never inject HTML, execute links, or interpret Markdown as markup/XAML.
        bool truncated = notes.Length > 40000;
        string bounded = truncated ? notes[..40000] : notes;
        var lines = bounded.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new StringBuilder();
        bool inCodeBlock = false;
        var code = new StringBuilder();
        int renderedLines = 0;

        void FlushParagraph()
        {
            if (paragraph.Length == 0) return;
            ReleaseNotesPanel.Children.Add(CreateText(CleanInline(paragraph.ToString()), 12, "#CCD9EA"));
            paragraph.Clear();
        }

        void FlushCode()
        {
            if (code.Length == 0) return;
            var body = CreateText(code.ToString().TrimEnd(), 11, "#B3CEE8");
            body.FontFamily = new FontFamily("Consolas");
            ReleaseNotesPanel.Children.Add(new Border
            {
                Child = body,
                Background = Brush.Parse("#0B1729"),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 4, 0, 4)
            });
            code.Clear();
        }

        foreach (string original in lines)
        {
            if (++renderedLines > 700) { truncated = true; break; }
            string line = original.TrimEnd();
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                if (inCodeBlock) FlushCode();
                inCodeBlock = !inCodeBlock;
                continue;
            }
            if (inCodeBlock)
            {
                code.AppendLine(line);
                continue;
            }
            if (trimmed.Length == 0)
            {
                FlushParagraph();
                continue;
            }
            var heading = HeadingPattern.Match(line);
            if (heading.Success)
            {
                FlushParagraph();
                int level = heading.Groups[1].Length;
                var label = CreateText(CleanInline(heading.Groups[2].Value), level == 1 ? 18 : level == 2 ? 16 : 14,
                    "#E5D0FF", FontWeight.SemiBold);
                label.Margin = new Thickness(0, 8, 0, 3);
                ReleaseNotesPanel.Children.Add(label);
                continue;
            }
            if (trimmed is "---" or "***" or "___")
            {
                FlushParagraph();
                ReleaseNotesPanel.Children.Add(new Border
                {
                    Height = 1,
                    Background = Brush.Parse("#344760"),
                    Margin = new Thickness(0, 7, 0, 7)
                });
                continue;
            }
            var bullet = BulletPattern.Match(line);
            if (bullet.Success)
            {
                FlushParagraph();
                var item = CreateText("•  " + CleanInline(bullet.Groups[1].Value), 12, "#CEDBEC");
                item.Margin = new Thickness(9, 0, 0, 0);
                ReleaseNotesPanel.Children.Add(item);
                continue;
            }
            var numbered = NumberedPattern.Match(line);
            if (numbered.Success)
            {
                FlushParagraph();
                var item = CreateText(numbered.Groups[1].Value + ".  " + CleanInline(numbered.Groups[2].Value),
                    12, "#CEDBEC");
                item.Margin = new Thickness(9, 0, 0, 0);
                ReleaseNotesPanel.Children.Add(item);
                continue;
            }
            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                var quote = CreateText(CleanInline(trimmed.TrimStart('>', ' ')), 12, "#AFC4DC");
                quote.Margin = new Thickness(13, 3, 0, 3);
                ReleaseNotesPanel.Children.Add(quote);
                continue;
            }
            if (paragraph.Length > 0) paragraph.Append(' ');
            paragraph.Append(trimmed);
        }
        FlushParagraph();
        FlushCode();
        if (truncated)
            ReleaseNotesPanel.Children.Add(CreateText(
                "Notes abrégées pour l'affichage. Clique sur « Voir les notes sur GitHub » pour le texte complet.",
                12, "#88DFE7", FontWeight.SemiBold));
    }

    private static string CleanInline(string text)
    {
        string cleaned = LinkPattern.Replace(text, "$1");
        cleaned = cleaned.Replace("**", "", StringComparison.Ordinal)
            .Replace("__", "", StringComparison.Ordinal)
            .Replace("`", "", StringComparison.Ordinal)
            .Replace("*", "", StringComparison.Ordinal);
        return cleaned.Trim();
    }

    private static TextBlock CreateText(string value, double fontSize, string color,
        FontWeight weight = FontWeight.Normal) => new()
    {
        Text = value,
        FontSize = fontSize,
        FontWeight = weight,
        Foreground = Brush.Parse(color),
        TextWrapping = TextWrapping.Wrap
    };
}
