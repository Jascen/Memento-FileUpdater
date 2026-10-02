namespace FileUpdaterClient.UserSettings;

//The player's saved choices as MainViewModel uses them, so tests can supply their own
public interface IUserSettings
{
    string InstallPath { get; }
    Preferences Preferences { get; }

    void Load();

    //Creates the install folder if needed and checks it can be written to
    bool EnsureInstallPathUsable(out string error);

    //Saves a folder the player picked. Returns false with a message if it can't be used
    bool TrySetInstallPath(string folder, out string error);
}

//The real thing: InstallLocation and Preferences, saved per user under %AppData%
public class SavedSettings : IUserSettings
{
    public string InstallPath => InstallLocation.Path;
    public Preferences Preferences => Preferences.Current;

    public void Load()
    {
        InstallLocation.Load();
        Preferences.Load();
    }

    public bool EnsureInstallPathUsable(out string error) => InstallLocation.EnsureUsable(out error);

    public bool TrySetInstallPath(string folder, out string error) => InstallLocation.TrySet(folder, out error);
}
