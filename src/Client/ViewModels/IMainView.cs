namespace FileUpdaterClient.ViewModels;

//What MainViewModel needs from the window: dialogs, opening links, and restarting the app
public interface IMainView
{
    //Shows the settings dialog and returns a newly picked install folder, or null if it didn't change
    Task<string?> ShowSettingsAsync(string folderError);

    Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText);

    void OpenUrl(Uri uri);

    //Closes the launcher so the new version, started by the updater, can take over
    void ShutdownForRestart();
}
