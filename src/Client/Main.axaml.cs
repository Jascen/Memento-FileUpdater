using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace FileUpdaterClient;

public partial class Main : Window
{
    private MainViewModel _data;
    private bool _folderReady;

    public Main()
    {
        InitializeComponent();
        DataContext = _data = new MainViewModel();
        Title = _data.Title;
        Opened += async (_, _) => await StartWhenFolderChosen();
    }

    private async Task StartWhenFolderChosen()
    {
        InstallLocation.Load();
        Preferences.Load();
        if (!InstallLocation.EnsureUsable(out var error))
        {
            //Wait for the player to pick another folder with the button instead of opening the picker on launch
            _data.ErrorMessage = error;
            _data.ProgressText = Settings.NoFolderChosen;
            _data.ChangeFolderText = Settings.ChooseFolder;
            return;
        }

        await StartUpdate();
    }

    private async Task StartUpdate()
    {
        _data.InstallPath = InstallLocation.Path;
        _data.ChangeFolderText = Settings.ChangeFolder;
        _folderReady = true;
        _data.LauncherInstalled = TazUOSetup.IsInstalled;

        if (Preferences.Current.VerifyOnLaunch)
            await UpdateHandler.HandleUpdates(_data);
        else
            _data.ProgressText = Settings.NotVerified;
    }

    //Returns true once a usable folder is saved
    private async Task<bool> ChooseFolder()
    {
        while (true)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Settings.ChooseFolderTitle,
                AllowMultiple = false,
                SuggestedStartLocation = await StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Documents),
            });

            var folder = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
            if (folder == null)
            {
                return false;
            }

            if (InstallLocation.TrySet(folder, out var error))
            {
                _data.ErrorMessage = string.Empty;
                return true;
            }

            _data.ErrorMessage = error;
        }
    }

    //The center button downloads pending updates first, then launches the game
    private async void MainButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_data.DownloadsReady)
            await UpdateHandler.DownloadUpdates();
        else
            await Play();
    }

    private async void ChangeFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (!_folderReady)
        {
            //The default folder wasn't usable, so start the update once one is picked
            if (await ChooseFolder()) await StartUpdate();
            return;
        }

        var previous = InstallLocation.Path;
        if (!await ChooseFolder() || InstallLocation.Path == previous) return;

        //The updater runs once per launch, so restart it to check the new folder
        UpdateHandler.Cancel();
        if (Environment.ProcessPath != null) Process.Start(Environment.ProcessPath);
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    // The window has no system title bar, so let it be dragged from anywhere that isn't a control
    private void Window_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void NavLink_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not NavLink link) return;

        if (link.Target == NavLink.VerifyAction)
        {
            if (_folderReady) _ = UpdateHandler.HandleUpdates(_data);
        }
        else
            OpenUrl(link.Target);
    }

    //Opens the TazUO launcher, first asking the player to confirm if the files weren't fully verified
    private async Task Play()
    {
        if (!_data.FilesVerified && Preferences.Current.WarnIfNotVerified)
        {
            var dialog = new ConfirmDialog(Settings.UnverifiedTitle, Settings.UnverifiedMessage,
                Settings.PlayAnyway, Settings.CancelText);
            if (!await dialog.ShowDialog<bool>(this)) return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(TazUOSetup.LauncherExecutable)
            {
                UseShellExecute = true,
                WorkingDirectory = TazUOSetup.LauncherDirectory
            });
        }
        catch (Exception ex)
        {
            _data.ErrorMessage = Settings.LaunchError;
            Console.WriteLine(ex.Message);
        }
    }

    private async void Settings_Click(object? sender, RoutedEventArgs e) => await new SettingsDialog().ShowDialog(this);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => UpdateHandler.Cancel();

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            _ = Launcher.LaunchUriAsync(uri);
    }
}
