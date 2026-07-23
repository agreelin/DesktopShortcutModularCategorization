using FolderSessionLock.App.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderSessionLock.App.Tests.ViewModels;

public sealed class MainViewModelTests
{
    [Fact]
    public void InitialState_DescribesStageTwoBoundary()
    {
        var viewModel = new MainViewModel(NullLogger<MainViewModel>.Instance);

        Assert.Equal("Folder Session Lock", viewModel.Title);
        Assert.Equal(
            "Stage 2 domain core ready. Folder locking UI is not implemented.",
            viewModel.StatusMessage);
    }

    [Fact]
    public void UpdateStatus_ChangesStatusAndRaisesNotification()
    {
        var viewModel = new MainViewModel(NullLogger<MainViewModel>.Instance);
        string? changedProperty = null;
        viewModel.PropertyChanged += (_, eventArgs) => changedProperty = eventArgs.PropertyName;

        viewModel.UpdateStatus("Ready for testing.");

        Assert.Equal("Ready for testing.", viewModel.StatusMessage);
        Assert.Equal(nameof(MainViewModel.StatusMessage), changedProperty);
    }

    [Fact]
    public void UpdateStatus_RejectsBlankText()
    {
        var viewModel = new MainViewModel(NullLogger<MainViewModel>.Instance);

        Assert.Throws<ArgumentException>(() => viewModel.UpdateStatus(" "));
    }
}
