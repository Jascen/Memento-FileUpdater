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
    private bool _updateStarted;

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
        _updateStarted = true;
        await UpdateHandler.HandleUpdates(_data);
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

    private async void ChangeFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (!_updateStarted)
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
            if (_updateStarted) _ = UpdateHandler.HandleUpdates(_data);
        }
        else
            OpenUrl(link.Target);
    }

    private void PrivacyPolicy_Click(object? sender, RoutedEventArgs e) => OpenUrl(Settings.PrivacyPolicyUrl);

    private void Play_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.GetFullPath(Settings.GameExecutable, InstallLocation.Path);
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path)
            });
        }
        catch (Exception ex)
        {
            _data.ErrorMessage = Settings.LaunchError;
            Console.WriteLine(ex.Message);
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => UpdateHandler.Cancel();

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            _ = Launcher.LaunchUriAsync(uri);
    }
}
