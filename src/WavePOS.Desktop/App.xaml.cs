using System.Windows;
using WavePOS.Core.Models;
using WavePOS.Data;
using WavePOS.Desktop.Printing;
using WavePOS.Desktop.Services;
using WavePOS.Desktop.ViewModels;

namespace WavePOS.Desktop;

/// <summary>
/// App flow: login window → till (main window) → log out → login window again.
/// Closing either window with the X exits the app.
/// </summary>
public partial class App : Application
{
    private PosDatabase _db = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // We swap between windows, so don't let closing one end the app automatically.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            // WAVEPOS_DB lets you point the app at a different database (e.g. for testing or training).
            var path = Environment.GetEnvironmentVariable("WAVEPOS_DB");
            _db = new PosDatabase(string.IsNullOrWhiteSpace(path) ? PosDatabase.DefaultPath : path);
            _db.Initialize();
            ShowLogin();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"WAVE POS could not start:\n\n{ex.Message}", "WAVE POS",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void ShowLogin()
    {
        var vm = new LoginViewModel(new UserRepository(_db), new SettingsRepository(_db).GetBusinessProfile());
        var login = new LoginWindow(vm);
        var loggedIn = false;

        vm.LoggedIn += user =>
        {
            loggedIn = true;
            ShowTill(user);
            login.Close();
        };
        login.Closed += (_, _) => { if (!loggedIn) Shutdown(); };

        MainWindow = login;
        login.Show();
    }

    private void ShowTill(User user)
    {
        var settings = new SettingsRepository(_db);
        var ui = new PosUi(_db, settings, new PrintService());
        var vm = new SalesViewModel(new ProductRepository(_db), new SaleRepository(_db), new DraftInvoiceRepository(_db),
            settings, ui, user);
        var till = new MainWindow { DataContext = vm };
        var loggingOut = false;

        vm.LogoutRequested += () =>
        {
            loggingOut = true;
            ShowLogin();
            till.Close();
        };
        till.Closed += (_, _) => { if (!loggingOut) Shutdown(); };

        MainWindow = till;
        till.Show();
    }
}
