using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Nexora.Gui.Dialogs;
using Nexora.Gui.Settings;
using Nexora.Gui.Services;
using Nexora.Gui.Updates;

namespace Nexora.Gui.DevTools;

/// <summary>
/// Nexora's frameless desktop toolbar. The left-hand brand column aligns with
/// the workspace sidebar. The flexible middle column is the window drag region;
/// the right-hand controls stay inset from the edge even when maximized.
/// </summary>
internal static class NexoraTitleBar
{
    public static void Attach(MainWindow window, UpdateCoordinator updater, IStickManager sticks)
    {
        if (window.Content is not Control original) return;

        AddDisconnectButton(original, window);
        window.SettingsRequested += () => SettingsWindow.ShowAsync(window, updater, sticks);
        window.UpdatesRequested += () => updater.OpenUpdateCenterAsync();
        window.InitializeWorkspaceNavigation();
        window.SystemDecorations = SystemDecorations.None;

        // Sidebar alignment: MainWindow.axaml uses a 224-DIP left column.
        // The final Auto column never competes for space with the drag region.
        var bar = new Grid
        {
            Height = 56,
            ColumnDefinitions = new ColumnDefinitions("224,*,Auto"),
            Background = Brush.Parse("#0E1525")
        };

        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center
        };
        using var logoStream = Avalonia.Platform.AssetLoader.Open(
            new Uri("avares://Nexora/Assets/Nexora_Icon.png"));
        var logoBitmap = new Avalonia.Media.Imaging.Bitmap(logoStream);
        brand.Children.Add(new Border
        {
            Width = 34,
            Height = 34,
            Padding = new Thickness(5),
            CornerRadius = new CornerRadius(10),
            Background = Brush.Parse("#211B40"),
            BorderBrush = Brush.Parse("#514181"),
            BorderThickness = new Thickness(1),
            Child = new Image
            {
                Source = logoBitmap,
                Stretch = Stretch.Uniform
            }
        });
        brand.Children.Add(new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = "NEXORA",
                    FontSize = 14,
                    FontWeight = FontWeight.Bold,
                    Foreground = Brush.Parse("#F3F0FF"),
                    LetterSpacing = 1.0
                },
                new TextBlock
                {
                    Text = "SECURITY WORKSPACE",
                    FontSize = 9,
                    Foreground = Brush.Parse("#8FA9CD")
                }
            }
        });
        var brandArea = new Border
        {
            Background = Brush.Parse("#0B1426"),
            Padding = new Thickness(16, 0, 10, 0),
            BorderBrush = Brush.Parse("#263A5A"),
            BorderThickness = new Thickness(0, 0, 1, 1),
            Child = brand
        };
        AttachDrag(brandArea, window);
        Grid.SetColumn(brandArea, 0);
        bar.Children.Add(brandArea);

        var workspace = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            VerticalAlignment = VerticalAlignment.Center
        };
        workspace.Children.Add(new TextBlock
        {
            Text = "Espace de travail",
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            Foreground = Brush.Parse("#E6ECFB"),
            VerticalAlignment = VerticalAlignment.Center
        });
        workspace.Children.Add(new Border
        {
            Background = Brush.Parse("#1A2540"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 5),
            Child = new TextBlock
            {
                Text = "v" + DisplayVersion,
                FontSize = 10,
                Foreground = Brush.Parse("#B3C7E8")
            }
        });
        if (DevEnabled)
        {
            workspace.Children.Add(new Border
            {
                Background = Brush.Parse("#32234C"),
                BorderBrush = Brush.Parse("#634596"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(7, 4),
                Child = new TextBlock
                {
                    Text = "DEV",
                    FontWeight = FontWeight.Bold,
                    FontSize = 10,
                    Foreground = Brush.Parse("#D9B8FF")
                }
            });
        }
        var dragArea = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(20, 0, 8, 0),
            Child = workspace
        };
        AttachDrag(dragArea, window);
        Grid.SetColumn(dragArea, 1);
        bar.Children.Add(dragArea);

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            // Inset the buttons to prevent clipping at the right screen edge.
            Margin = new Thickness(0, 0, 18, 0)
        };

        var update = MakeButton("↻  Mises à jour", 125, "#202D46");
        ToolTip.SetTip(update, "Vérifier les nouvelles versions de Nexora");
        update.Click += async (_, _) => await updater.OpenUpdateCenterAsync();
        controls.Children.Add(update);
        updater.AvailabilityChanged += available =>
        {
            void Refresh()
            {
                update.Content = available ? "↓  Mise à jour" : "↻  Mises à jour";
                update.Background = Brush.Parse(available ? "#174942" : "#202D46");
                update.BorderBrush = Brush.Parse(available ? "#2A8170" : "#34435F");
            }
            if (Dispatcher.UIThread.CheckAccess()) Refresh();
            else Dispatcher.UIThread.Post(Refresh);
        };

        var settings = MakeButton("⚙  Paramètres", 116, "#202D46");
        settings.Click += async (_, _) => await SettingsWindow.ShowAsync(window, updater, sticks);
        controls.Children.Add(settings);

        // Compact window controls make room for the action buttons.
        var minimize = MakeButton("—", 37, "#151F34");
        ToolTip.SetTip(minimize, "Réduire la fenêtre");
        minimize.Click += (_, _) => window.WindowState = WindowState.Minimized;
        controls.Children.Add(minimize);

        var maximize = MakeButton("□", 37, "#151F34");
        ToolTip.SetTip(maximize, "Agrandir ou restaurer la fenêtre");
        maximize.Click += (_, _) =>
        {
            ToggleMaximize(window);
            maximize.Content = window.WindowState == WindowState.Maximized ? "❐" : "□";
        };
        controls.Children.Add(maximize);

        var close = MakeButton("✕", 39, "#422436");
        close.Foreground = Brush.Parse("#FFC3CF");
        close.BorderBrush = Brush.Parse("#633C53");
        ToolTip.SetTip(close, "Fermer la fenêtre (Nexora reste dans la zone de notification)");
        close.Click += (_, _) => window.Close(); // Preserves existing tray and vault-lock behavior.
        controls.Children.Add(close);

        Grid.SetColumn(controls, 2);
        bar.Children.Add(controls);

        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };
        // Detach the old visual tree before re-parenting it.
        window.Content = null;
        Grid.SetRow(bar, 0);
        layout.Children.Add(bar);
        Grid.SetRow(original, 1);
        layout.Children.Add(original);
        window.Content = layout;
    }

    private static void AttachDrag(Border area, MainWindow window)
    {
        area.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(window).Properties.IsLeftButtonPressed)
                window.BeginMoveDrag(e);
        };
        area.DoubleTapped += (_, _) => ToggleMaximize(window);
    }

#if NEXORA_DEV
    private const bool DevEnabled = true;
#else
    private const bool DevEnabled = false;
#endif

    // Assembly version is read from Nexora.Gui.csproj, never hard-coded here.
    internal static string DisplayVersion
    {
        get
        {
            var v = typeof(App).Assembly.GetName().Version;
            return v is null ? "inconnue" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
    }

    private static Button MakeButton(string caption, double width, string background) => new()
    {
        Content = caption,
        Width = width,
        Height = 36,
        Padding = new Thickness(7, 5),
        Margin = new Thickness(0),
        FontSize = 11,
        FontWeight = FontWeight.SemiBold,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        CornerRadius = new CornerRadius(9),
        BorderThickness = new Thickness(1),
        BorderBrush = Brush.Parse("#34435F"),
        Background = Brush.Parse(background),
        Foreground = Brush.Parse("#E7EEFC")
    };

    // Keep the current security behavior: disconnect all USB/IP keys, lock the vault,
    // then terminate the desktop lifetime, instead of merely hiding to the tray.
    private static void AddDisconnectButton(Control original, MainWindow window)
    {
        var button = new Button
        {
            Content = "⏻  Déconnexion",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 2, 0, 0),
            Background = Brush.Parse("#392233"),
            Foreground = Brushes.White
        };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                await window.ForceCloseAsync();
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    desktop.Shutdown();
            }
            catch (Exception ex)
            {
                button.IsEnabled = true;
                await MessageWindow.ShowAsync(window, "Déconnexion impossible", ex.Message);
            }
        };

        if (!InsertAfterLock(original, button))
            DevConsole.Write("WARN", "disconnect-button-not-found");
    }

    private static bool InsertAfterLock(Control parent, Button exitButton)
    {
        if (parent is StackPanel stack)
        {
            for (int i = 0; i < stack.Children.Count; i++)
            {
                if (stack.Children[i] is Button b && b.Content is string caption
                    && caption.Contains("Verrouiller", StringComparison.OrdinalIgnoreCase))
                {
                    stack.Children.Insert(i + 1, exitButton);
                    return true;
                }
            }
        }
        if (parent is Panel panel)
        {
            foreach (var child in panel.Children)
                if (InsertAfterLock(child, exitButton)) return true;
        }
        else if (parent is Decorator decorator && decorator.Child is Control child)
        {
            if (InsertAfterLock(child, exitButton)) return true;
        }
        else if (parent is ContentControl content && content.Content is Control control)
        {
            if (InsertAfterLock(control, exitButton)) return true;
        }
        return false;
    }

    private static void ToggleMaximize(Window window) => window.WindowState =
        window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
