using System.Diagnostics;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Win32;
using Nexora.Gui.Dialogs;
using Nexora.Gui.DevTools;

namespace Nexora.Gui.Updates;

/// <summary>Updates are a separate user-approved workflow; no vault/PIN content is sent to GitHub.</summary>
internal sealed class UpdateCoordinator : IDisposable
{
    private const string UninstallRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Nexora";
    private readonly MainWindow _mainWindow;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly Func<bool> _anyConnectedKeys;
    private readonly UpdateReleaseClient _client = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private UpdatePreferences _preferences = UpdatePreferences.Load();
    private bool _dialogVisible;
    private UpdateReleaseClient.Release? _availableRelease;
    public event Action<bool>? AvailabilityChanged;
    private int _started;

    internal bool AutoInstall => _preferences.AutoInstall;
    internal bool IncludePrereleases => _preferences.IncludePrereleases;
    internal bool CheckOnStartup => _preferences.CheckOnStartup;
    internal bool CheckEveryHour => _preferences.CheckEveryHour;

    internal async Task SaveSettingsAsync(bool autoInstall, bool includePrereleases,
        bool checkOnStartup, bool checkEveryHour)
    {
        bool channelChanged = _preferences.IncludePrereleases != includePrereleases;
        _preferences.AutoInstall = autoInstall;
        _preferences.IncludePrereleases = includePrereleases;
        _preferences.CheckOnStartup = checkOnStartup;
        _preferences.CheckEveryHour = checkEveryHour;
        if (channelChanged) _preferences.DismissedTag = string.Empty;
        _preferences.Save();
        DevConsole.Write("UPDATE", "preferences-saved", includePrereleases ? "prereleases=enabled" : "prereleases=disabled");
        if (channelChanged)
        {
            _availableRelease = null;
            AvailabilityChanged?.Invoke(false);
            await CheckAsync(manual: false, suppressPopup: true);
        }
    }

    internal async Task RestoreDefaultSettingsAsync()
    {
        _preferences = new UpdatePreferences();
        _preferences.Save();
        _availableRelease = null;
        AvailabilityChanged?.Invoke(false);
        await CheckAsync(manual: false, suppressPopup: true);
    }

    public UpdateCoordinator(MainWindow mainWindow, IClassicDesktopStyleApplicationLifetime desktop,
        Func<bool> anyConnectedKeys)
    {
        _mainWindow = mainWindow;
        _desktop = desktop;
        _anyConnectedKeys = anyConnectedKeys;
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) _ = PeriodicCheckAsync();
    }

    public async Task CheckNowAsync() => await CheckAsync(manual: true);

    public async Task OpenUpdateCenterAsync()
    {
        if (_availableRelease is { } cached)
        {
            await ShowReleaseAsync(cached);
            return;
        }
        await CheckNowAsync();
    }

    private async Task PeriodicCheckAsync()
    {
        try
        {
            // An immediate check on startup, then exactly one check per hour while running.
            if (_preferences.CheckOnStartup)
                await CheckAsync(manual: false);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
            while (await timer.WaitForNextTickAsync(_stop.Token))
                if (_preferences.CheckEveryHour)
                    await CheckAsync(manual: false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task CheckAsync(bool manual, bool suppressPopup = false)
    {
        // An explicit click must not disappear while the hourly check is running.
        if (manual) await _checkGate.WaitAsync(_stop.Token);
        else if (!await _checkGate.WaitAsync(0)) return;
        try
        {
            var version = typeof(App).Assembly.GetName().Version ?? new Version(1, 3, 5);
            DevConsole.Write("UPDATE", "check-start");
            var release = await _client.GetLatestAsync(version, _preferences.IncludePrereleases, _stop.Token);
            _availableRelease = release;
            Dispatcher.UIThread.Post(() => AvailabilityChanged?.Invoke(release is not null));
            if (release is null)
            {
                DevConsole.Write("UPDATE", "up-to-date");
                if (manual) Dispatcher.UIThread.Post(async () =>
                    await MessageWindow.ShowAsync(_mainWindow, "Nexora · Mise à jour", _preferences.IncludePrereleases
                        ? "Aucune nouvelle version (stable ou préversion) disponible sur GitHub."
                        : "Aucune nouvelle version stable disponible sur GitHub."));
                return;
            }
            if (!manual && string.Equals(release.Tag, _preferences.DismissedTag, StringComparison.OrdinalIgnoreCase))
                return;
            DevConsole.Write("UPDATE", "update-detected");
            // A detected update changes the title-bar button; show the changelog on click.
            // Only explicit user opt-in enables unattended installation.
            if (manual || (_preferences.AutoInstall && !suppressPopup))
                Dispatcher.UIThread.Post(() => _ = ShowReleaseAsync(release));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            DevConsole.Write("WARN", "update-check-failed", $"{ex.GetType().Name}; HRESULT=0x{ex.HResult:X8}");
            if (manual)
                Dispatcher.UIThread.Post(async () => await MessageWindow.ShowAsync(_mainWindow,
                    "Nexora · Mise à jour", "Vérification impossible : " + ex.Message));
            // Network errors must never prevent the normal application from running.
        }
        finally { _checkGate.Release(); }
    }

    private async Task ShowReleaseAsync(UpdateReleaseClient.Release release)
    {
        if (_dialogVisible || _stop.IsCancellationRequested) return;
        _dialogVisible = true;
        try
        {
            _mainWindow.OpenFromTray();
            string? destination = DetectManagedInstallDirectory();
            bool unattended = _preferences.AutoInstall && destination is not null && !_anyConnectedKeys();
            var window = new UpdateWindow(release, _preferences.AutoInstall, destination is not null,
                dialog => InstallAsync(release, destination!, dialog),
                value => { _preferences.AutoInstall = value; _preferences.Save(); },
                () => { _preferences.DismissedTag = release.Tag; _preferences.Save(); },
                unattended);
            await window.ShowDialog(_mainWindow);
        }
        catch (Exception ex)
        {
            await MessageWindow.ShowAsync(_mainWindow, "Nexora · Mise à jour", ex.Message);
        }
        finally { _dialogVisible = false; }
    }

    private static string? DetectManagedInstallDirectory()
    {
#if NEXORA_DEV
        // DEV releases may be previewed but must never silently overwrite stable FIDO2 installations.
        return null;
#else
        if (!OperatingSystem.IsWindows()) return null;
        using var key = Registry.CurrentUser.OpenSubKey(UninstallRegistryPath);
        var installed = key?.GetValue("InstallLocation") as string;
        var running = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(installed) || string.IsNullOrWhiteSpace(running)) return null;
        var executable = Path.GetFullPath(Path.Combine(installed, "Nexora.exe"));
        if (!string.Equals(executable, Path.GetFullPath(running), StringComparison.OrdinalIgnoreCase)) return null;
        if (!File.Exists(Path.Combine(installed, ".nexora-install-state.json"))) return null;
        return Path.GetFullPath(installed);
#endif
    }

    private async Task InstallAsync(UpdateReleaseClient.Release release, string destination, UpdateWindow dialog)
    {
        DevConsole.Write("UPDATE", "download-start");
        var progress = new Progress<int>(value => dialog.SetProgress(value));
        var setup = await _client.DownloadVerifiedAsync(release, progress, _stop.Token);
        // Reconfirm nothing changed during download (important for FIDO2 USB/IP sessions).
        if (_anyConnectedKeys())
        {
            dialog.SetStatus("Déconnectez les clés USB virtuelles actives avant de continuer.");
            throw new InvalidOperationException("Une ou plusieurs clés sont encore connectées.");
        }
        if (!string.Equals(DetectManagedInstallDirectory(), destination, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Le dossier d'installation Nexora a changé.");
        dialog.SetStatus("Intégrité validée. Fermeture de Nexora et lancement de l'installateur…");
        var info = new ProcessStartInfo(setup)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(setup)!
        };
        info.ArgumentList.Add("--apply-update");
        info.ArgumentList.Add("--parent-pid=" + Environment.ProcessId);
        info.ArgumentList.Add("--target=" + destination);
        // Launch the verified Setup first, so a failure to start does not close Nexora.
        using var installer = Process.Start(info)
            ?? throw new IOException("Impossible de démarrer Nexora-Setup.exe.");
        DevConsole.Write("UPDATE", "installer-start");
        await _mainWindow.ForceCloseAsync(); // Detaches all USB/IP devices and locks the vault.
        _desktop.Shutdown();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _client.Dispose();
        // An in-flight check may still be releasing its semaphore after cancellation.
        // Leave the small synchronisation objects to GC rather than disposing them early.
    }
}
