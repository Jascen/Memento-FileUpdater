using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace FileUpdaterClient;

public partial class Main : Window
{
    private MainViewModel _data;
    public Main()
    {
        InitializeComponent();
        DataContext = _data = new MainViewModel();
        Title = _data.Title;
        _ = UpdateHandler.HandleUpdates(_data);
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
            _ = UpdateHandler.HandleUpdates(_data);
        else
            OpenUrl(link.Target);
    }

    private void PrivacyPolicy_Click(object? sender, RoutedEventArgs e) => OpenUrl(Settings.PrivacyPolicyUrl);

    private void Play_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.GetFullPath(Settings.GameExecutable, AppDomain.CurrentDomain.BaseDirectory);
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
