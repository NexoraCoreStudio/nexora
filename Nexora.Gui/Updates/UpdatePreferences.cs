using System.Text.Json;

namespace Nexora.Gui.Updates;

internal sealed class UpdatePreferences
{
    public bool AutoInstall { get; set; }
    public string DismissedTag { get; set; } = "";

    private static string ConfigFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Nexora", "update-preferences.json");

    public static UpdatePreferences Load()
    {
        try
        {
            return File.Exists(ConfigFile)
                ? JsonSerializer.Deserialize<UpdatePreferences>(File.ReadAllText(ConfigFile)) ?? new()
                : new();
        }
        catch (Exception)
        {
            return new(); // Corrupt preference files must never unlock auto-install.
        }
    }

    public void Save()
    {
        var path = ConfigFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
