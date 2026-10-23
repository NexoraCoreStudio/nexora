using System.Text.Json;

namespace Nexora.Gui.Updates;

/// <summary>Local preferences only; never stores credential material.</summary>
internal sealed class UpdatePreferences
{
    public bool AutoInstall { get; set; } = false;
    public bool IncludePrereleases { get; set; } = false;
    public bool CheckOnStartup { get; set; } = true;
    public bool CheckEveryHour { get; set; } = true;
    public string DismissedTag { get; set; } = string.Empty;

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
            // Corrupt settings can never enable prereleases or silent updates.
            return new();
        }
    }

    public void Save()
    {
        string file = ConfigFile;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, file, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
