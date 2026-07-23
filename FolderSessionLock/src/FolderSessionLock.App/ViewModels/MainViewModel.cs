using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace FolderSessionLock.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ILogger<MainViewModel> _logger;
    private string _statusMessage = "Stage 2 domain core ready. Folder locking UI is not implemented.";

    public MainViewModel(ILogger<MainViewModel> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title => "Folder Session Lock";

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (_statusMessage == value)
            {
                return;
            }

            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public void UpdateStatus(string statusMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statusMessage);

        StatusMessage = statusMessage;
        _logger.LogInformation(
            "Application status changed to {StatusMessage}.",
            statusMessage);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
