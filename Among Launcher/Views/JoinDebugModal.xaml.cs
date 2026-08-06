using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Text.RegularExpressions;

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
            if (bold)
            {
                var run = new Run(text + "\n") { FontWeight = FontWeights.Bold };
                StatusTextBlock.Inlines.Add(run);
            }
            else
            {
                var parts = Regex.Split(text, @"(\*\*.*?\*\*)");
                foreach (var part in parts)
                {
                    if (string.IsNullOrEmpty(part)) continue;

                    if (part.StartsWith("**") && part.EndsWith("**"))
                    {
                        var inner = part[2..^2];
                        StatusTextBlock.Inlines.Add(new Run(inner + "\n") { FontWeight = FontWeights.Bold });
                    }
                    else
                    {
                        StatusTextBlock.Inlines.Add(new Run(part));
                    }
                }
                StatusTextBlock.Inlines.Add(new Run("\n"));
            }

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
