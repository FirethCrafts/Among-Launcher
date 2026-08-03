using System.Windows;
using System.Windows.Controls;
using AmongLauncher.Models;

namespace AmongLauncher.Views;

public partial class LibraryPickerModal : UserControl
{
    public IReadOnlyList<LibraryEntry> Entries { get; }

    public event EventHandler<LibraryEntry>? PickRequested;

    public LibraryPickerModal(IEnumerable<LibraryEntry> entries)
    {
        InitializeComponent();
        Entries = entries.ToList();
        DataContext = this;
    }

    private void PickButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is LibraryEntry entry)
            PickRequested?.Invoke(this, entry);
    }
}
