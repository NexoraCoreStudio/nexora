using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Gui.Configuration;
using Nexora.Gui.Dialogs;
using Nexora.Gui.Services;

namespace Nexora.Gui;

public partial class App : Application
{
    // Nexora resource embedded in the Nexora GUI assembly.
    private static readonly Uri IconUri = new("avares://Nexora/Assets/Nexora_Icon.png");

    private TrayIcon? _trayIcon;
    private ServiceProvider? _services;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = BuildServiceProvider();
            var mainWindow = _services.GetRequiredService<MainWindow>();
            desktop.MainWindow = mainWindow;
            SetupTrayIcon(mainWindow, desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IStickConfigStore, StickConfigStore>();
        services.AddSingleton<ICredentialPrompt, WindowCredentialPrompt>();
        services.AddSingleton<IStickManager, StickManager>();
        services.AddTransient<MainWindow>();
        return services.BuildServiceProvider();
    }

    private void SetupTrayIcon(MainWindow mainWindow, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var showItem = new NativeMenuItem("Ouvrir Nexora");
        showItem.Click += (_, _) => ShowMainWindow(mainWindow);

        var exitItem = new NativeMenuItem("Quitter Nexora");
        exitItem.Click += (_, _) =>
        {
            mainWindow.ForceClose();
            desktop.Shutdown();
        };

        var menu = new NativeMenu { Items = { showItem, exitItem } };

        _trayIcon = new TrayIcon
        {
            Icon = LoadIcon(),
            ToolTipText = "Nexora · Virtual Security Key",
            Menu = menu,
        };
        _trayIcon.Clicked += (_, _) => ShowMainWindow(mainWindow);
    }

    private static void ShowMainWindow(MainWindow window)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private static WindowIcon LoadIcon()
    {
        using var stream = AssetLoader.Open(IconUri);
        return new WindowIcon(new Bitmap(stream));
    }
}
