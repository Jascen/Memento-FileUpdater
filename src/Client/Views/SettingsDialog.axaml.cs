using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FileUpdaterClient.Config;
using FileUpdaterClient.Updating;
using FileUpdaterClient.UserSettings;

namespace FileUpdaterClient.Views;

//Edits a copy of the saved preferences and install folder so Cancel leaves them untouched.
//ShowDialog<string?> returns the newly picked install folder when the player saves a different one, otherwise null
public partial class SettingsDialog : Window
{
    private readonly Preferences _edited;
    private string _installFolder = InstallLocation.Path;
    private string _savedIgnoreList = string.Empty; //As read from the folder, so an untouched list isn't written back

    public SettingsDialog()
    {
        InitializeComponent();
        DataContext = _edited = new Preferences
        {
            VerifyOnLaunch = Preferences.Current.VerifyOnLaunch,
            WarnIfNotVerified = Preferences.Current.WarnIfNotVerified,
            AllowInsecureDownloads = Preferences.Current.AllowInsecureDownloads,
        };
        InstallPathText.Text = _installFolder;
        ChangeFolderButton.Content = Strings.ChangeFolder;
        LoadIgnoreList();
    }

    //The ignore list lives in the install folder, so it shows the one in the folder that is picked
    private void LoadIgnoreList()
    {
        try
        {
            var path = IgnoreRules.PathIn(_installFolder);
            _savedIgnoreList = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Couldn't read the ignore list: {e.Message}");
            _savedIgnoreList = string.Empty;
        }

        IgnoreText.Text = _savedIgnoreList;
    }

    private void SaveIgnoreList()
    {
        var text = IgnoreText.Text ?? string.Empty;
        if (text == _savedIgnoreList) return;

        try
        {
            Directory.CreateDirectory(_installFolder);
            File.WriteAllText(IgnoreRules.PathIn(_installFolder), text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Couldn't save the ignore list: {e.Message}");
        }
    }

    //Shows why the current folder can't be used, e.g. when the default folder isn't writable on first launch
    public void ShowFolderError(string error)
    {
        FolderErrorText.Text = error;
        FolderErrorText.IsVisible = !string.IsNullOrEmpty(error);
    }

    private async void ChangeFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.ChooseFolderTitle,
            AllowMultiple = false,
            SuggestedStartLocation = await GetStartFolder(),
        });

        var folder = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (folder == null) return;

        if (!InstallLocation.CanUse(folder, out var error))
        {
            ShowFolderError(error);
            return;
        }

        ShowFolderError(string.Empty);
        _installFolder = folder;
        InstallPathText.Text = folder;
        LoadIgnoreList();
    }

    //Opens the picker in the folder shown in the dialog, falling back to Documents if it doesn't exist yet
    private async Task<IStorageFolder?> GetStartFolder()
    {
        if (Directory.Exists(_installFolder))
        {
            var current = await StorageProvider.TryGetFolderFromPathAsync(_installFolder);
            if (current != null) return current;
        }

        return await StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Documents);
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        Preferences.Save(_edited);
        SaveIgnoreList();
        Close(_installFolder != InstallLocation.Path ? _installFolder : null);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
