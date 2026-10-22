using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Nexora.Gui.Updates;

namespace Nexora.Gui.DevTools;

/// <summary>A compact custom window title bar with native-like window controls and an update action.</summary>
internal static class NexoraTitleBar
{
    public static void Attach(MainWindow window, UpdateCoordinator updater)
    {
        if (window.Content is not Control original) return;
        // Our own decoration permits the Update button beside minimize / maximize / close.
        window.SystemDecorations = SystemDecorations.None;
        var bar = new Grid
        {
            Height = 43,
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"),
            Background = Brush.Parse("#111B30")
        };
        var drag = new Border { Background = Brushes.Transparent, Padding = new Thickness(16, 10) };
        drag.Child = new TextBlock
        {
            Text = "NEXORA  ·  VIRTUAL SECURITY KEY" + (DevEnabled ? "    [DEV]" : ""),
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            Foreground = Brush.Parse("#C3CFFC"),
            VerticalAlignment = VerticalAlignment.Center
        };
        drag.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) window.BeginMoveDrag(e);
        };
        drag.DoubleTapped += (_, _) => ToggleMaximize(window);
        bar.Children.Add(drag);
        var update = MakeButton("↻  Vérifier", "#23384F", 125);
        update.Click += async (_, _) => await updater.OpenUpdateCenterAsync();
        Grid.SetColumn(update, 1); bar.Children.Add(update);
        updater.AvailabilityChanged += available =>
        {
            update.Content = available ? "⬇  UPDATE" : "↻  Vérifier";
            update.Background = Brush.Parse(available ? "#136C61" : "#23384F");
        };
        var minimize = MakeButton("—", "#17263B", 40);
        minimize.Click += (_, _) => window.WindowState = WindowState.Minimized;
        Grid.SetColumn(minimize, 2); bar.Children.Add(minimize);
        var maximize = MakeButton("□", "#17263B", 40);
        maximize.Click += (_, _) => ToggleMaximize(window);
        Grid.SetColumn(maximize, 3); bar.Children.Add(maximize);
        var close = MakeButton("✕", "#66252F", 43);
        close.Click += (_, _) => window.Close(); // Existing close handler locks the vault.
        Grid.SetColumn(close, 4); bar.Children.Add(close);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        // Detach first: Avalonia does not permit a control to have two visual parents.
        window.Content = null;
        layout.Children.Add(bar);
        Grid.SetRow(original, 1);
        layout.Children.Add(original);
        window.Content = layout;
    }

#if NEXORA_DEV
    private const bool DevEnabled = true;
#else
    private const bool DevEnabled = false;
#endif

    private static Button MakeButton(string text, string background, double width) => new()
    {
        Content = text,
        Width = width,
        Height = 36,
        Margin = new Thickness(2, 3),
        FontSize = 12,
        Background = Brush.Parse(background),
        Foreground = Brushes.White,
        Padding = new Thickness(7, 5)
    };

    private static void ToggleMaximize(Window window) => window.WindowState =
        window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
