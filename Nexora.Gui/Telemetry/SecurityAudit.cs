using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;

namespace Nexora.Gui.Telemetry;

// Audit metadata only. Never record PINs, CBOR payloads, credentials or key material.
public sealed class SecurityAudit
{
    public static SecurityAudit Instance { get; } = new();
    private readonly object _sync = new();
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nexora", "royal-audit.jsonl");

    private SecurityAudit() { }

    public void Record(string category, string outcome, string description = "")
    {
        // Only fixed identifiers, never user-supplied account data in the log.
        var allowed = new[] { "APP", "PIN", "USB", "FIDO" };
        if (!allowed.Contains(category)) return;
        var entry = new Entry(DateTimeOffset.UtcNow, category, outcome.Length > 32 ? outcome[..32] : outcome,
            description.Length > 100 ? description[..100] : description);
        try
        {
            lock (_sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, JsonSerializer.Serialize(entry) + Environment.NewLine);
                // Retain only the latest ~1 MB to limit growth.
                if (new FileInfo(_path).Length > 1024 * 1024)
                {
                    var lines = File.ReadLines(_path).TakeLast(2000).ToArray();
                    File.WriteAllLines(_path, lines);
                }
            }
        }
        catch (IOException) { /* Audit failure must not approve or deny FIDO automatically. */ }
        catch (UnauthorizedAccessException) { }
    }

    public IReadOnlyList<Entry> Recent(int count = 20)
    {
        lock (_sync)
        {
            if (!File.Exists(_path)) return Array.Empty<Entry>();
            try
            {
                return File.ReadLines(_path).TakeLast(Math.Clamp(count, 1, 500))
                    .Select(line => { try { return JsonSerializer.Deserialize<Entry>(line); } catch (JsonException) { return null; } })
                    .Where(x => x is not null).Select(x => x!).Reverse().ToArray();
            }
            catch (IOException) { return Array.Empty<Entry>(); }
            catch (UnauthorizedAccessException) { return Array.Empty<Entry>(); }
        }
    }

    public sealed record Entry(DateTimeOffset Time, string Category, string Outcome, string Description);
}
