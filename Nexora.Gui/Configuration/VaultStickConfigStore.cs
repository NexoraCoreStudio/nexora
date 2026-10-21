using System.Text.Json;
using System.Text.Json.Serialization;
using Nexora.Gui.Security;

namespace Nexora.Gui.Configuration;

/// <summary>Stores config and stick metadata ONLY in Nexora.vault, never beside the EXE.</summary>
public sealed class VaultStickConfigStore : IStickConfigStore
{
    private readonly VaultAccessController _access;
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };
    public VaultStickConfigStore(VaultAccessController access) => _access = access;
    private static string ConfigPath(string name) => "config/sticks/" + name + ".json";

    public IReadOnlyList<StickConfig> LoadAll()
    {
        lock (_access.SyncRoot)
        {
            var session = _access.Session;
            return session.ListPaths().Where(p => p.StartsWith("config/sticks/", StringComparison.Ordinal) && p.EndsWith(".json", StringComparison.Ordinal))
                .Select(p =>
                {
                    byte[]? bytes = session.ReadFile(p);
                    if (bytes is null) return null;
                    try { return JsonSerializer.Deserialize<StickConfig>(bytes, Options); }
                    finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
                })
                .Where(c => c is not null).Select(c => c!).ToArray();
        }
    }

    public StickConfig Create(string name, string serialNumber, SecretManagerConfig secretManager)
    {
        if (!StickConfigStore.IsValidStickName(name)) throw new ArgumentException("Nom de cle invalide.", nameof(name));
        if (secretManager is not FileSecretManagerConfig)
            throw new NotSupportedException("Nexora 1.2 ne cree que des cles locales protegees par Nexora.vault.");
        lock (_access.SyncRoot)
        {
            if (_access.Session.ReadFile(ConfigPath(name)) is not null) throw new IOException("Cette cle existe deja.");
            var config = new StickConfig { Name = name, SerialNumberIdentifier = serialNumber, SecretManager = secretManager };
            Save(config);
            return config;
        }
    }

    public void Save(StickConfig config)
    {
        if (!StickConfigStore.IsValidStickName(config.Name)) throw new ArgumentException("Nom invalide.");
        lock (_access.SyncRoot)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(config, Options);
            try { _access.Session.WriteFile(ConfigPath(config.Name), bytes); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
        }
    }

    public void Delete(string name)
    {
        if (!StickConfigStore.IsValidStickName(name)) throw new ArgumentException("Nom invalide.");
        lock (_access.SyncRoot)
        {
            var session = _access.Session;
            // Remove all credentials, private keys, certificate material and metadata for this stick.
            foreach (var path in session.ListPaths().Where(p => p.StartsWith("sticks/" + name + "/", StringComparison.Ordinal)))
                session.DeleteFile(path);
            session.DeleteFile(ConfigPath(name));
        }
    }

    // These APIs are deliberately forbidden: using them would recreate plaintext directories.
    public string GetStickDirectory(string name) => throw new NotSupportedException("Aucun dossier de cle en clair dans Nexora 1.2.");
    public string GetKeyStoreDirectory(string name) => GetStickDirectory(name);
    public string GetCredentialStoreDirectory(string name) => GetStickDirectory(name);
    public string GetAttestationCertificateDirectory(string name) => GetStickDirectory(name);
}
