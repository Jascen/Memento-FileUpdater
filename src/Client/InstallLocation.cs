using System.Text.Json;

namespace FileUpdaterClient;

//The folder the game files are installed into: a "Client" folder next to the exe unless the player picked another one.
//Saved per user (e.g. %AppData%/UODiablo/updater.json) so it survives moving or re-downloading the updater.
public static class InstallLocation
{
    private static readonly string ConfigPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Settings.AppDataFolder, "updater.json");

    //Used until the player picks a folder: a "Client" folder next to the updater exe
    public static readonly string DefaultPath = System.IO.Path.Combine(AppContext.BaseDirectory, Settings.DefaultInstallFolder);

    public static string Path { get; private set; } = DefaultPath;

    public static void Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;

            var saved = JsonSerializer.Deserialize<SavedLocation>(File.ReadAllText(ConfigPath));
            if (!string.IsNullOrEmpty(saved?.InstallPath)) Path = saved.InstallPath;
        }
        catch (Exception e)
        {
            Console.WriteLine($"Failed to read {ConfigPath}: {e.Message}");
        }
    }

    //Creates the current folder if needed and checks it can be written to
    public static bool EnsureUsable(out string error) => CheckWritable(Path, out error);

    //Checks a folder the player picked without saving it
    public static bool CanUse(string folder, out string error) => CheckWritable(folder, out error);

    //Saves a folder the player picked. Returns false with a message if it can't be used
    public static bool TrySet(string folder, out string error)
    {
        if (!CheckWritable(folder, out error)) return false;

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new SavedLocation { InstallPath = folder }));
        }
        catch (Exception e)
        {
            //Still use it for this run, it just won't be remembered
            Console.WriteLine($"Failed to save {ConfigPath}: {e.Message}");
        }

        Path = folder;
        return true;
    }

    private static bool CheckWritable(string folder, out string error)
    {
        error = string.Empty;
        try
        {
            Directory.CreateDirectory(folder);

            //Make sure we can actually write there before downloading anything, e.g. Program Files needs admin rights
            var probe = System.IO.Path.Combine(folder, ".write-test");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
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
