using System.Text.Json;
using FileUpdaterClient.Config;
using FileUpdaterClient.Updating;

namespace FileUpdaterClient.UserSettings;

//Remembers which version of the TazUO launcher was installed from the server, in %AppData%/<AppDataFolder>/packages.json.
//Kept per user, not in the install folder, so moving the install folder doesn't make it look out of date.
public class PackageState : IPackageState
{
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LauncherConfig.AppDataFolder, "packages.json");

    public Version? ClientVersion
    {
        get
        {
            try
            {
                if (!File.Exists(StatePath)) return null;
                var saved = JsonSerializer.Deserialize<SavedState>(File.ReadAllText(StatePath));
                return Version.TryParse(saved?.ClientVersion, out var version) ? version : null;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Failed to read {StatePath}: {e.Message}");
                return null;
            }
        }
    }

    public void SetClientVersion(Version version)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(new SavedState { ClientVersion = version.ToString() }));
        }
        catch (Exception e)
        {
            //The client is installed either way, it just gets reinstalled next time
            Console.WriteLine($"Failed to save {StatePath}: {e.Message}");
        }
    }

    private class SavedState
    {
        public string ClientVersion { get; set; } = string.Empty;
    }
}
