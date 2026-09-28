using System.Text.Json;
using FileUpdaterClient.Config;

namespace FileUpdaterClient.UserSettings;

//Player choices from the settings dialog, saved per user next to the install folder choice (e.g. %AppData%/UODiablo/settings.json)
public class Preferences
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LauncherConfig.AppDataFolder, "settings.json");

    public static Preferences Current { get; private set; } = new();

    public bool VerifyOnLaunch { get; set; } = true;
    public bool WarnIfNotVerified { get; set; } = true;

    public static void Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                Current = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(ConfigPath)) ?? new Preferences();
        }
        catch (Exception e)
        {
            Console.WriteLine($"Failed to read {ConfigPath}: {e.Message}");
        }
    }

    public static void Save(Preferences preferences)
    {
        Current = preferences;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(preferences));
        }
        catch (Exception e)
        {
            //Still used for this run, it just won't be remembered
            Console.WriteLine($"Failed to save {ConfigPath}: {e.Message}");
        }
    }
}
