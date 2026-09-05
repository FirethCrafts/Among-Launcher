using System.Windows;
using System.Windows.Controls;

namespace AmongLauncher.Views;

public partial class UpdateProgressModal : UserControl
{
    public UpdateProgressModal()
    {
        InitializeComponent();
    }

    public void SetStatus(string status)
    {
        StatusText.Text = status;
    }

    public void SetProgress(int progress)
    {
        ProgressBar.Value = Math.Clamp(progress, 0, 100);
    }
}
