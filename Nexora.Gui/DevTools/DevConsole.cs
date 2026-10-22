using System.Runtime.InteropServices;
using System.Text;

namespace Nexora.Gui.DevTools;

/// <summary>
/// A real Windows console for deliberately built development binaries only.
/// Never print PINs, vault values, CTAP payloads, tokens or credentials.
/// </summary>
internal static class DevConsole
{
#if NEXORA_DEV
    private static readonly object Gate = new();
    private static bool _ready;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    public static void Start()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (Gate)
        {
            if (_ready) return;
            if (!AllocConsole()) return; // The process may already own a console.
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.Title = "NEXORA DEV | Diagnostics (sans données sensibles)";
            _ready = true;
            Write("BOOT", "console", "Mode développeur actif");
        }
    }

    public static void Write(string category, string action, string? detail = null)
    {
        lock (Gate)
        {
            if (!_ready) return;
            var color = category switch
            {
                "ERROR" => ConsoleColor.Red,
                "WARN" => ConsoleColor.Yellow,
                "USB" => ConsoleColor.Cyan,
                "FIDO" => ConsoleColor.Magenta,
                "PIN" => ConsoleColor.DarkYellow,
                "UPDATE" => ConsoleColor.Green,
                _ => ConsoleColor.Gray
            };
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{DateTime.Now:HH:mm:ss.fff}] ");
            Console.ForegroundColor = color;
            Console.Write($"[{category}] ");
            Console.ForegroundColor = ConsoleColor.White;
            // Callers must pass constant operation names, never raw untrusted payloads.
            Console.WriteLine(action + (string.IsNullOrWhiteSpace(detail) ? "" : " | " + detail));
            Console.ForegroundColor = previous;
        }
    }
#else
    public static void Start() { }
    public static void Write(string category, string action, string? detail = null) { }
#endif
}
