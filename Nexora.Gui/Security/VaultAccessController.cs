using Nexora.Crypt;

namespace Nexora.Gui.Security;

/// <summary>
/// One authenticated, shared session for the FIDO backend and GUI. No plaintext key directory.
/// Authenticated access is NOT proof of TPM-bound, offline-brute-force resistant security.
/// </summary>
public sealed class VaultAccessController : IDisposable
{
    public static VaultAccessController Instance { get; } = new();
    public readonly object SyncRoot = new();
    private VaultSession? _session;

    private VaultAccessController() { }

    public bool IsConfigured => File.Exists(VaultStore.DefaultVaultPath);
    /// <summary>Checks the actual DPAPI slot, not just a forgeable local file.</summary>
    public bool IsAuthorizedOnThisWindowsProfile =>
        IsConfigured && VaultStore.IsAuthorizedOnThisWindowsProfile(VaultStore.DefaultVaultPath);
    public bool IsUnlocked { get { lock (SyncRoot) return _session is not null; } }

    public VaultSession Session
    {
        get { lock (SyncRoot) return _session ?? throw new InvalidOperationException("Nexora.vault est verrouille."); }
    }

    public event Action? AccessRevoked;

    public VaultCreationResult Create(string pin)
    {
        lock (SyncRoot)
        {
            if (_session is not null) throw new InvalidOperationException("Vault is already unlocked.");
            var result = VaultStore.Create(VaultStore.DefaultVaultPath, pin);
            _session = VaultStore.OpenOnAuthorizedComputer(VaultStore.DefaultVaultPath, pin);
            return result;
        }
    }

    public void Open(string pin, string? recoveryCode)
    {
        lock (SyncRoot)
        {
            if (_session is not null) throw new InvalidOperationException("Vault already unlocked.");
            _session = string.IsNullOrWhiteSpace(recoveryCode)
                ? VaultStore.OpenOnAuthorizedComputer(VaultStore.DefaultVaultPath, pin)
                : VaultStore.OpenWithRecoveryCode(VaultStore.DefaultVaultPath, pin, recoveryCode);
        }
    }

    public bool VerifyApproval(string pin, out string error)
    {
        lock (SyncRoot)
        {
            if (_session is null) { error = "Coffre verrouille."; return false; }
            try
            {
                if (_session.VerifyPin(pin, out var blocked)) { error = string.Empty; return true; }
                error = $"PIN incorrect. Prochaine tentative : {blocked:dd/MM HH:mm}.";
            }
            catch (Exception)
            {
                error = "Verification PIN impossible. Le coffre sera verrouille.";
            }
            // Invalidate the session before queuing USB/IP disconnection.
            _session.Dispose();
            _session = null;
        }
        AccessRevoked?.Invoke();
        return false;
    }

    public void ChangePin(string oldPin, string newPin)
    {
        try
        {
            lock (SyncRoot) Session.ChangePin(oldPin, newPin);
        }
        catch (InvalidVaultPinException)
        {
            Lock();
            AccessRevoked?.Invoke();
            throw;
        }
    }

    public void Record(VaultSecurityEvent eventType)
    {
        lock (SyncRoot)
        {
            if (_session is not null) _session.RecordSecurityEvent(eventType);
        }
    }

    public void Lock()
    {
        lock (SyncRoot)
        {
            if (_session is null) return;
            try { _session.RecordSecurityEvent(VaultSecurityEvent.ManualLock); }
            finally { _session.Dispose(); _session = null; }
        }
    }

    public void Dispose() => Lock();
}
