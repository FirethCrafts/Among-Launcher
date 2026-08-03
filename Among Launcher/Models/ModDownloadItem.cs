using CommunityToolkit.Mvvm.ComponentModel;

namespace AmongLauncher.Models;

public partial class ModDownloadItem : ObservableObject
{
    public string Url { get; }
    public string FileName { get; }

    [ObservableProperty]
    private string _status = "Pending";

    [ObservableProperty]
    private int _progress;

    [ObservableProperty]
    private bool _isActive;

    public ModDownloadItem(string url, string fileName)
    {
        Url = url;
        FileName = fileName;
    }
}
