using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using AmongLauncher.GameDetection;

namespace AmongLauncher.Views;

public partial class StorefrontPickerModal : UserControl
{
    public event EventHandler<GameSearchResult>? Selected;

    public StorefrontPickerModal()
    {
        InitializeComponent();
    }

    public void SetResults(IEnumerable<GameSearchResult> results)
    {
        StorefrontList.ItemsSource = results;
    }

    private void StorefrontRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: GameSearchResult result })
        {
            Selected?.Invoke(this, result);
        }
    }
}
