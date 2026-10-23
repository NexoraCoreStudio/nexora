using System.Text.Json;
using Nexora.Fido.Ctap2;
using Nexora.Gui.Security;
using Nexora.Gui.DevTools;

namespace Nexora.Gui.Telemetry;

/// <summary>
/// Security audit plus successful WebAuthn usage (relying-party domains only).
/// History is encrypted inside Nexora.vault and visible only while unlocked.
/// </summary>
public sealed class SecurityAudit
{
    private const string HistoryVaultPath = "history/sites-v1.json";
    private const int MaxSiteEntries = 300;
    public static SecurityAudit Instance { get; } = new();

    private SecurityAudit() => RelyingPartyActivity.Succeeded += RecordRelyingPartyUse;

    public void Record(string category, string outcome, string description = "")
    {
        // Never print PINs, raw CTAP messages, secrets, serials or URLs.
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

    private void RecordRelyingPartyUse(string operation, string rpId)
    {
        var access = VaultAccessController.Instance;
        lock (access.SyncRoot)
        {
            if (!access.IsUnlocked) return;
            try
            {
                var events = ReadSitesLocked(access);
                events.Add(new SiteEvent(DateTimeOffset.UtcNow, rpId, operation));
                if (events.Count > MaxSiteEntries)
                    events.RemoveRange(0, events.Count - MaxSiteEntries);
                access.Session.WriteFile(HistoryVaultPath, JsonSerializer.SerializeToUtf8Bytes(events));
                DevConsole.Write("FIDO", "relying-party-history-saved", operation);
            }
            catch (Exception)
            {
                // Never allow telemetry failure to invalidate a successful FIDO2 request.
                DevConsole.Write("WARN", "relying-party-history-not-saved");
            }
        }
    }

    private static List<SiteEvent> ReadSitesLocked(VaultAccessController access)
    {
        var data = access.Session.ReadFile(HistoryVaultPath);
        if (data is null || data.Length == 0) return [];
        return JsonSerializer.Deserialize<List<SiteEvent>>(data)
            ?? throw new InvalidDataException("Historique WebAuthn invalide.");
    }

    public IReadOnlyList<Entry> Recent(int count = 20)
    {
        var access = VaultAccessController.Instance;
        lock (access.SyncRoot)
        {
            if (!access.IsUnlocked) return Array.Empty<Entry>();
            int limit = Math.Clamp(count, 1, 500);
            var entries = access.Session.GetAudit()
                .Where(e => e.EventType != "vault-entry-written")
                .Select(e => new Entry(e.WhenUtc, "SEC", e.EventType, ""))
                .ToList();
            try
            {
                foreach (var site in ReadSitesLocked(access))
                {
                    string action = site.Operation == "inscription" ? "Inscription" : "Connexion";
                    entries.Add(new Entry(site.TimeUtc, "SITE", action, site.RpId));
                }
            }
            catch (Exception)
            {
                entries.Add(new Entry(DateTimeOffset.UtcNow, "SITE", "Historique illisible", "Vérifiez le coffre chiffré."));
            }
            return entries.OrderByDescending(e => e.Time).Take(limit).ToArray();
        }
    }

    public sealed record SiteEvent(DateTimeOffset TimeUtc, string RpId, string Operation);
    public sealed record Entry(DateTimeOffset Time, string Category, string Outcome, string Description);
}
