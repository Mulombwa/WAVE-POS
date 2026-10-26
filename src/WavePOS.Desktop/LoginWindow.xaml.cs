using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WavePOS.Desktop.ViewModels;

namespace WavePOS.Desktop;

public partial class LoginWindow : Window
{
    private readonly LoginViewModel _vm;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    public LoginWindow(LoginViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;

        _clock.Tick += (_, _) => UpdateClock();
        _clock.Start();
        UpdateClock();

        Loaded += (_, _) => (_vm.IsSetupMode ? (Control)NameBox : UsernameBox).Focus();
        Closed += (_, _) => _clock.Stop();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("HH:mm", CultureInfo.CurrentCulture);
        DateText.Text = now.ToString("dddd, d MMMM yyyy", CultureInfo.CurrentCulture);
    }

    private async void OnSubmit(object sender, RoutedEventArgs e)
    {
        await _vm.SubmitAsync(PasswordBox.Password, ConfirmBox.Password);

        // On a failed attempt clear the PIN so the next try starts fresh.
        if (_vm.HasError)
        {
            PasswordBox.Clear();
            ConfirmBox.Clear();
            PasswordBox.Focus();
        }
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (PasswordBox.Password.Length > 0) _vm.ErrorMessage = "";
    }

    /// <summary>Keypad types into the confirm box if it has focus, otherwise the PIN box.</summary>
    private PasswordBox TargetBox => ConfirmBox.IsKeyboardFocusWithin ? ConfirmBox : PasswordBox;

    private void OnKeypad(object sender, RoutedEventArgs e)
    {
        var box = TargetBox;
        box.Password += (string)((Button)sender).Content;
        box.Focus();
    }

    private void OnKeypadBackspace(object sender, RoutedEventArgs e)
    {
        var box = TargetBox;
        if (box.Password.Length > 0) box.Password = box.Password[..^1];
        box.Focus();
    }

    private void OnKeypadClear(object sender, RoutedEventArgs e)
    {
        var box = TargetBox;
        box.Clear();
        box.Focus();
    }
}
