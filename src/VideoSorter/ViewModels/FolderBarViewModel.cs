using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VideoSorter.ViewModels;

public partial class FolderBarViewModel : ObservableObject
{
    private readonly Action<string> _onFolderChanged;

    [ObservableProperty]
    private string _currentPath = string.Empty;

    public FolderBarViewModel(Action<string> onFolderChanged)
    {
        _onFolderChanged = onFolderChanged;
    }

    [RelayCommand]
    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Video Folder"
        };

        if (dialog.ShowDialog() == true)
        {
            NavigateTo(dialog.FolderName);
        }
    }

    public void NavigateTo(string path)
    {
        CurrentPath = path;
        _onFolderChanged(path);
    }
}
