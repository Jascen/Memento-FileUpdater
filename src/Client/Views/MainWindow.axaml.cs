using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using FileUpdaterClient.Config;
using FileUpdaterClient.ViewModels;

namespace FileUpdaterClient.Views;

//View only: forwards clicks to MainViewModel and provides the dialogs, links and restart it asks for
public partial class MainWindow : Window, IMainView
{
    private readonly MainViewModel _viewModel;

    public MainWindow() : this(new MainViewModel()) //For the XAML previewer
    {
    }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        Title = viewModel.Title;
        Opened += async (_, _) => await _viewModel.StartAsync(this);
    }

    public Task<string?> ShowSettingsAsync(string folderError)
    {
        var dialog = new SettingsDialog();
        dialog.ShowFolderError(folderError);
        return ShowModal(dialog.ShowDialog<string?>(this));
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText) =>
        ShowModal(new ConfirmDialog(title, message, confirmText, cancelText).ShowDialog<bool>(this));

    public void OpenUrl(Uri uri) => _ = Launcher.LaunchUriAsync(uri);

    public void ShutdownForRestart() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();

    //Dims the launcher while a dialog is open
    private async Task<T> ShowModal<T>(Task<T> dialog)
    {
        _viewModel.IsDialogOpen = true;
        try
        {
            return await dialog;
        }
        finally
        {
            _viewModel.IsDialogOpen = false;
        }
    }

    // The window has no system title bar, so let it be dragged from anywhere that isn't a control
    private void Window_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private async void NavLink_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is NavLink link)
            await _viewModel.OpenLinkAsync(link);
    }

    private async void MainButton_Click(object? sender, RoutedEventArgs e) => await _viewModel.MainButtonAsync();

    private async void Settings_Click(object? sender, RoutedEventArgs e) => await _viewModel.OpenSettingsAsync();

    private async void UpdateLauncher_Click(object? sender, RoutedEventArgs e) => await _viewModel.UpdateLauncherAsync();

    private void DismissLauncherUpdate_Click(object? sender, RoutedEventArgs e) => _viewModel.DismissLauncherUpdate();

    private void Cancel_Click(object? sender, RoutedEventArgs e) => _viewModel.CancelUpdate();

    private async void Retry_Click(object? sender, RoutedEventArgs e) => await _viewModel.RetryAsync();

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
