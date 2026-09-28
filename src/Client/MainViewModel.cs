using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FileUpdaterClient;

public class MainViewModel : INotifyPropertyChanged
{
    private string _title = Settings.Title;
    private string _subTitle = Settings.Subtitle;
    private string _titleColor = Settings.TitleColor;
    private string _errorMessage = string.Empty;
    private string _subtitleColor = Settings.SubtitleColor;
    private double _progress;
    private double _fileProgress;
    private string _progressText = "Checking for updates..";
    private string _fileProgressText = string.Empty;
    private bool _isUpdating;
    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<NavLink> Links { get; } = Settings.Links;
    public string PlayText { get; } = Settings.PlayText;
    public string PrivacyPolicyText { get; } = Settings.PrivacyPolicyText;
    public bool HasPrivacyPolicy { get; } = !string.IsNullOrEmpty(Settings.PrivacyPolicyUrl);

    public string TitleColor
    {
        get => _titleColor;
        set => SetField(ref _titleColor, value);
    }

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    public string SubtitleColor
    {
        get => _subtitleColor;
        set => SetField(ref _subtitleColor, value);
    }

    public string Subtitle
    {
        get => _subTitle;
        set => SetField(ref _subTitle, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetField(ref _errorMessage, value);
    }

    /// <summary>Overall progress across all files (blue bar), 0-100.</summary>
    public double Progress
    {
        get => _progress;
        set => SetField(ref _progress, value);
    }

    public string ProgressText
    {
        get => _progressText;
        set => SetField(ref _progressText, value);
    }

    /// <summary>Progress of the file currently being downloaded (red bar), 0-100.</summary>
    public double FileProgress
    {
        get => _fileProgress;
        set => SetField(ref _fileProgress, value);
    }

    public string FileProgressText
    {
        get => _fileProgressText;
        set => SetField(ref _fileProgressText, value);
    }

    public bool IsUpdating
    {
        get => _isUpdating;
        set
        {
            if (SetField(ref _isUpdating, value))
                OnPropertyChanged(nameof(CanPlay));
        }
    }

    public bool CanPlay => !IsUpdating && !string.IsNullOrEmpty(Settings.GameExecutable);

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
