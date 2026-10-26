using System.Windows;

namespace WavePOS.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Keep the search box focused so a barcode scanner can type into it at any time.
        Loaded += (_, _) => SearchBox.Focus();
    }
}
