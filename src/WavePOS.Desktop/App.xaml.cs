using System.Windows;
using WavePOS.Data;
using WavePOS.Desktop.ViewModels;

namespace WavePOS.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var db = new PosDatabase(PosDatabase.DefaultPath);
            db.Initialize();

            var vm = new SalesViewModel(new ProductRepository(db), new SaleRepository(db));
            new MainWindow { DataContext = vm }.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"WAVE POS could not start:\n\n{ex.Message}", "WAVE POS",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
