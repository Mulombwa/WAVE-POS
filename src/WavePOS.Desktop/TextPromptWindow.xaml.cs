using System.Windows;

namespace WavePOS.Desktop;

/// <summary>Small dialog asking for one line of text.</summary>
public partial class TextPromptWindow : Window
{
    public TextPromptWindow(string title, string prompt, string confirmText)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        OkButton.Content = confirmText;
        Loaded += (_, _) => ValueBox.Focus();
    }

    public string Value => ValueBox.Text;

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
