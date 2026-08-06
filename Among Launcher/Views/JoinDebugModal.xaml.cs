using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace AmongLauncher.Views;

public partial class JoinDebugModal : UserControl
{
    private Action? _onPlay;

    public JoinDebugModal()
    {
        InitializeComponent();
    }

    public void AppendLine(string text, bool bold = false)
    {
        Dispatcher.Invoke(() =>
        {
            var run = new Run(text + "\n")
            {
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal
            };
            StatusTextBlock.Inlines.Add(run);
            LogScrollViewer.ScrollToEnd();
        });
    }

    public void ShowPlayButton(Action onClick)
    {
        Dispatcher.Invoke(() =>
        {
            _onPlay = onClick;
            PlayButton.Visibility = Visibility.Visible;
        });
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        PlayButton.IsEnabled = false;
        var mainWindow = Window.GetWindow(this) as MainWindow;
        mainWindow?.ModalOverlayControl.Hide();

        if (_onPlay != null)
            await Task.Run(_onPlay);
    }
}
