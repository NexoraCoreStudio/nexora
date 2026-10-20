using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace Nexora.Gui.Security;

// UI PIN, NOT a substitute for a CTAP2 authenticator PIN or hardware-backed protection.
public sealed class LocalPinGuard
{
    private static readonly Lazy<LocalPinGuard> LazyInstance = new(() => new LocalPinGuard());
    public static LocalPinGuard Instance => LazyInstance.Value;
    private readonly object _sync = new();
    private readonly string _file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nexora", "ui-pin.json");
    private PinRecord? _record;
    private int _failures;
    private DateTimeOffset _lockedUntil;
    private LocalPinGuard()
    {
        try { if (File.Exists(_file)) _record = JsonSerializer.Deserialize<PinRecord>(File.ReadAllText(_file)); }
        catch (Exception ex) { throw new InvalidOperationException("Impossible de lire ui-pin.json. Accès refusé par sécurité.", ex); }
    }
    public bool IsConfigured => _record != null;
    private static bool ValidPin(string pin) => pin.Length == 4 && pin.All(c => c is >= '0' and <= '9');
    private static PinRecord CreateRecord(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, 310_000, HashAlgorithmName.SHA256, 32);
        return new PinRecord(Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }
    private void Save(PinRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(record));
        File.Move(temp, _file, true);
        _record = record;
    }
    public bool TryInitialize(string pin, out string error)
    {
        lock (_sync)
        {
            error = string.Empty;
            if (_record != null) { error = "Le PIN existe déjà."; return false; }
            if (!ValidPin(pin)) { error = "Saisis exactement quatre chiffres."; return false; }
            Save(CreateRecord(pin));
            return true;
        }
    }
    public bool Verify(string pin, out string error)
    {
        lock (_sync)
        {
            error = string.Empty;
            if (_record == null) { error = "Configure le PIN d'abord."; return false; }
            var now = DateTimeOffset.UtcNow;
            if (now < _lockedUntil) { error = $"Trop d'essais : attends {Math.Ceiling((_lockedUntil-now).TotalSeconds)} s."; return false; }
            bool good = false;
            if (ValidPin(pin))
            {
                var salt = Convert.FromBase64String(_record.Salt);
                var expected = Convert.FromBase64String(_record.Hash);
                var supplied = Rfc2898DeriveBytes.Pbkdf2(pin, salt, 310_000, HashAlgorithmName.SHA256, expected.Length);
                good = CryptographicOperations.FixedTimeEquals(expected, supplied);
            }
            if (good) { _failures = 0; return true; }
            _failures++;
            if (_failures >= 5) { _failures = 0; _lockedUntil = now.AddMinutes(1); error = "5 échecs : pause de 60 secondes."; }
            else error = "PIN incorrect.";
            return false;
        }
    }
    public bool Change(string oldPin, string newPin, out string error)
    {
        lock (_sync)
        {
            if (!Verify(oldPin, out error)) return false;
            if (!ValidPin(newPin)) { error = "Le nouveau PIN doit contenir quatre chiffres."; return false; }
            if (oldPin == newPin) { error = "Choisis un autre PIN."; return false; }
            Save(CreateRecord(newPin));
            error = string.Empty;
            return true;
        }
    }
    private sealed record PinRecord(string Salt, string Hash);
}
