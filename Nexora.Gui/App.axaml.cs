using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Gui.Configuration;
using Nexora.Gui.Dialogs;
using Nexora.Gui.Services;
using Nexora.Gui.Security;
using Nexora.Gui.Updates;
using Nexora.Gui.DevTools;
using Nexora.Gui.Settings;

namespace Nexora.Gui;

public partial class App : Application
{
    private static readonly Uri IconUri = new("avares://Nexora/Assets/Nexora_Icon.png");
    private TrayIcon? _trayIcon;
    private ServiceProvider? _services;
    private UpdateCoordinator? _updater;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DevConsole.Start();
#if NEXORA_DEV
            DevCtapLogTarget.Install();
#endif
            DevConsole.Write("BOOT", "application-start");
            _services = BuildServiceProvider();
            var mainWindow = _services.GetRequiredService<MainWindow>();
            desktop.MainWindow = mainWindow;
            var manager = _services.GetRequiredService<IStickManager>();
            _updater = new UpdateCoordinator(mainWindow, desktop, () =>
            {
                // If connection status is unknown, never attempt an unattended update.
                try
                {
                    if (!VaultAccessController.Instance.IsUnlocked) return false;
                    return manager.GetConfiguredSticks().Any(s => manager.IsConnected(s.Name));
                }
                catch { return true; }
            });
            NexoraTitleBar.Attach(mainWindow, _updater, manager);
            SetupTrayIcon(mainWindow, desktop);
            mainWindow.Opened += (_, _) => _updater.Start();
            desktop.Exit += (_, _) =>
            {
                DevConsole.Write("BOOT", "application-stop");
                _updater?.Dispose();
                _services?.Dispose();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(VaultAccessController.Instance);
        services.AddSingleton<IStickConfigStore, VaultStickConfigStore>();
        services.AddSingleton<ICredentialPrompt, WindowCredentialPrompt>();
        services.AddSingleton<IStickManager, StickManager>();
        services.AddTransient<MainWindow>();
        return services.BuildServiceProvider();
    }

    private void SetupTrayIcon(MainWindow mainWindow, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var showItem = new NativeMenuItem("Ouvrir Nexora");
        showItem.Click += (_, _) => Dispatcher.UIThread.Post(mainWindow.OpenFromTray);

        var manageItem = new NativeMenuItem("Gérer mes clés");
        manageItem.Click += (_, _) => Dispatcher.UIThread.Post(mainWindow.ManageKeysFromTray);

        var addItem = new NativeMenuItem("Ajouter une clé...");
        addItem.Click += (_, _) => Dispatcher.UIThread.Post(async () => await RunTrayActionAsync(mainWindow, mainWindow.AddKeyFromTrayAsync));

        var refreshItem = new NativeMenuItem("Actualiser les clés");
        refreshItem.Click += (_, _) => Dispatcher.UIThread.Post(mainWindow.RefreshFromTray);

        var historyItem = new NativeMenuItem("Historique de sécurité");
        historyItem.Click += (_, _) => Dispatcher.UIThread.Post(async () => await RunTrayActionAsync(mainWindow, mainWindow.HistoryFromTrayAsync));

        var updateItem = new NativeMenuItem("Vérifier les mises à jour");
        updateItem.Click += (_, _) => Dispatcher.UIThread.Post(async () =>
        {
            mainWindow.OpenFromTray();
            if (_updater != null) await _updater.CheckNowAsync();
        });

        var settingsItem = new NativeMenuItem("Paramètres...");
        settingsItem.Click += (_, _) => Dispatcher.UIThread.Post(async () =>
        {
            mainWindow.OpenFromTray();
            if (_updater is not null && _services is not null)
                await SettingsWindow.ShowAsync(mainWindow, _updater,
                    _services.GetRequiredService<IStickManager>());
        });

        var lockItem = new NativeMenuItem("Verrouiller Nexora");
        lockItem.Click += (_, _) => Dispatcher.UIThread.Post(async () => await RunTrayActionAsync(mainWindow, mainWindow.LockFromTrayAsync));

        var exitItem = new NativeMenuItem("Quitter Nexora");
        exitItem.Click += (_, _) => Dispatcher.UIThread.Post(async () =>
        {
            if (await RunTrayActionAsync(mainWindow, mainWindow.ForceCloseAsync))
                desktop.Shutdown();
        });

        var menu = new NativeMenu
        {
            Items =
            {
                showItem, manageItem, addItem, refreshItem, historyItem,
                updateItem, settingsItem, lockItem, exitItem
            }
        };

        _trayIcon = new TrayIcon
        {
            Icon = LoadIcon(),
            ToolTipText = "Nexora · Virtual Security Key",
            Menu = menu,
        };
        _trayIcon.Clicked += (_, _) => Dispatcher.UIThread.Post(mainWindow.OpenFromTray);
    }

    private static async Task<bool> RunTrayActionAsync(MainWindow mainWindow, Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception ex)
        {
            mainWindow.OpenFromTray();
            await MessageWindow.ShowAsync(mainWindow, "Nexora - Opération impossible", ex.Message);
            return false;
        }
    }

    private static WindowIcon LoadIcon()
    {
        using var stream = AssetLoader.Open(IconUri);
        return new WindowIcon(new Bitmap(stream));
    }
}
