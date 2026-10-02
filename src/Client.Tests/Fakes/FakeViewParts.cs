using FileUpdaterClient.TazUO;
using FileUpdaterClient.UserSettings;
using FileUpdaterClient.ViewModels;

namespace FileUpdaterClient.Tests.Fakes;

//The player's saved choices, held in memory
public class FakeSettings : IUserSettings
{
    public string InstallPath { get; set; } = string.Empty;
    public Preferences Preferences { get; } = new();
    public bool Usable { get; set; } = true; //Whether the current install folder can be written to

    public void Load()
    {
    }

    public bool EnsureInstallPathUsable(out string error)
    {
        error = Usable ? string.Empty : "not writable";
        return Usable;
    }

    public bool TrySetInstallPath(string folder, out string error)
    {
        error = string.Empty;
        InstallPath = folder;
        Usable = true;
        return true;
    }
}

//Stands in for the window: answers dialogs as told and records what it was asked to do
public class FakeMainView : IMainView
{
    public string? FolderToPick { get; set; } //What the settings dialog returns, null for no change
    public bool ConfirmAnswer { get; set; }
    public List<string> Confirmations { get; } = new(); //The title of each confirm dialog shown
    public int SettingsShown { get; private set; }
    public bool ShutDown { get; private set; }

    public Task<string?> ShowSettingsAsync(string folderError)
    {
        SettingsShown++;
        return Task.FromResult(FolderToPick);
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText)
    {
        Confirmations.Add(title);
        return Task.FromResult(ConfirmAnswer);
    }

    public void OpenUrl(Uri uri)
    {
    }

    public void ShutdownForRestart() => ShutDown = true;
}

public class FakeGameLauncher : IGameLauncher
{
    public bool IsInstalled { get; set; } = true;
    public int Starts { get; private set; }

    public void Start() => Starts++;
}

//Runs everything straight away on the calling thread, there is no UI thread in a test
public class ImmediateUiThread : IUiThread
{
    public void Post(Action action) => action();

    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
