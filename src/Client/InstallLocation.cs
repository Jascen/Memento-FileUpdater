using System.Text.Json;

namespace FileUpdaterClient;

//Remembers the folder the player chose to install the game files into.
//Saved per user (e.g. %AppData%/UODiablo/updater.json) so it survives moving or re-downloading the updater.
public static class InstallLocation
{
    private static readonly string ConfigPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Settings.AppDataFolder, "updater.json");

    public static string Path { get; private set; } = string.Empty;

    public static bool IsSet => !string.IsNullOrEmpty(Path) && Directory.Exists(Path);

    public static void Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;

            var saved = JsonSerializer.Deserialize<SavedLocation>(File.ReadAllText(ConfigPath));
            Path = saved?.InstallPath ?? string.Empty;
        }
        catch (Exception e)
        {
            Console.WriteLine($"Failed to read {ConfigPath}: {e.Message}");
        }
    }

    //Returns false with a message if the folder can't be used
    public static bool TrySet(string folder, out string error)
    {
        error = string.Empty;
        try
        {
            Directory.CreateDirectory(folder);

            //Make sure we can actually write there before downloading anything, e.g. Program Files needs admin rights
            var probe = System.IO.Path.Combine(folder, ".write-test");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new SavedLocation { InstallPath = folder }));
            Path = folder;
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine($"Can't use install folder {folder}: {e.Message}");
            error = string.Format(Settings.FolderNotWritable, folder);
            return false;
        }
    }

    private class SavedLocation
    {
        public string InstallPath { get; set; } = string.Empty;
    }
}
