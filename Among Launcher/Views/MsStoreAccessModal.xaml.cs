using System.Windows;
using System.Windows.Controls;
using AmongLauncher.GameDetection;

namespace AmongLauncher.Views;

public partial class MsStoreAccessModal : UserControl
{
    public MsStoreAccessModal()
    {
        InitializeComponent();
    }

    public void Configure(Storefront? storefront)
    {
        // Stub — Task 4 fills in the real text.
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var mainWindow = Window.GetWindow(this) as MainWindow;
        mainWindow?.ModalOverlayControl.Hide();
    }
}
