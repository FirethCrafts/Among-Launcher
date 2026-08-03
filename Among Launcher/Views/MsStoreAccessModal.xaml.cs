using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using AmongLauncher.GameDetection;

namespace AmongLauncher.Views;

public partial class MsStoreAccessModal : UserControl
{
    public MsStoreAccessModal()
    {
        InitializeComponent();

        GuideLink.RequestNavigate += GuideLink_RequestNavigate;
    }

    public void Configure(Storefront? storefront)
    {
        if (storefront == Storefront.Epic)
        {
            ExplanationText.Text =
                "The Epic Games Launcher shows Among Us as installed, but the launcher couldn't find a readable " +
                "game folder. This can happen when the game is installed to an unusual location or the launcher " +
                "hasn't finished installing it.";
            AnswerText.Text =
                "Open Epic Games Launcher, confirm the Among Us install location, then click Install again.";
        }
        else
        {
            ExplanationText.Text =
                "Among Us from the Microsoft Store lives in a protected Windows folder that's locked by default, " +
                "so the launcher can't copy the game files to make a modded install.";
            AnswerText.Text =
                "Run the launcher as administrator, or grant read access to that one folder with takeown and " +
                "icacls (see the guide), then click Install again. Mods are not guaranteed to work on the " +
                "Microsoft Store version.";
        }
    }

    private void GuideLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open guide URL: {ex.Message}");
        }

        e.Handled = true;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var mainWindow = Window.GetWindow(this) as MainWindow;
        mainWindow?.ModalOverlayControl.Hide();
    }
}
