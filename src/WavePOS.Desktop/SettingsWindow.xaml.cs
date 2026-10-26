using System.Windows;
using Microsoft.Win32;
using WavePOS.Desktop.Branding;
using WavePOS.Desktop.ViewModels;

namespace WavePOS.Desktop;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        Loaded += (_, _) => NameBox.Focus();
    }

    private void OnChooseLogo(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose your logo", Filter = Logo.FileFilter };
        if (dialog.ShowDialog(this) == true) _vm.ChooseLogo(dialog.FileName);
    }

    private void OnUseWaveLogo(object sender, RoutedEventArgs e) => _vm.UseWaveLogo();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_vm.Save()) DialogResult = true;
    }
}
