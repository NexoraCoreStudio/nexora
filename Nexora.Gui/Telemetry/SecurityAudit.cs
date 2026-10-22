using Nexora.Gui.Security;
using Nexora.Gui.DevTools;

namespace Nexora.Gui.Telemetry;

/// <summary>Encrypted audit in the vault; development telemetry includes event types only.</summary>
public sealed class SecurityAudit
{
    public static SecurityAudit Instance { get; } = new();
    private SecurityAudit() { }

    public void Record(string category, string outcome, string description = "")
    {
        // Never write PINs, raw CTAP messages, secrets, serials or URLs.
        if (category is not ("APP" or "PIN" or "USB" or "FIDO")) return;
        string safe = new string(outcome.ToLowerInvariant()
            .Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')
            .Take(25).ToArray());
        if (safe.Length == 0) safe = "event";
        DevConsole.Write(category, safe);
        var access = VaultAccessController.Instance;
        lock (access.SyncRoot)
        {
            if (access.IsUnlocked) access.Session.RecordAudit(category.ToLowerInvariant() + "-" + safe);
        }
    }

    public IReadOnlyList<Entry> Recent(int count = 20)
    {
        var access = VaultAccessController.Instance;
        lock (access.SyncRoot)
        {
            if (!access.IsUnlocked) return Array.Empty<Entry>();
            return access.Session.GetAudit().TakeLast(Math.Clamp(count, 1, 500)).Reverse()
                .Select(e => new Entry(e.WhenUtc, "SEC", e.EventType, "")).ToArray();
        }
    }

    public sealed record Entry(DateTimeOffset Time, string Category, string Outcome, string Description);
}
