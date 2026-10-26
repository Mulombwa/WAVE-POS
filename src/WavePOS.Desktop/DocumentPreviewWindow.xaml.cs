using System.Windows;
using System.Windows.Controls;
using WavePOS.Core.Documents;
using WavePOS.Core.Models;
using WavePOS.Desktop.Branding;
using WavePOS.Desktop.Printing;
using WavePOS.Desktop.ViewModels;

namespace WavePOS.Desktop;

/// <summary>Shows a receipt or invoice as it will print, with a choice of paper size and a Print button.</summary>
public partial class DocumentPreviewWindow : Window
{
    private readonly SalesDocument _document;
    private readonly BusinessProfile _business;
    private readonly PrintSettings _print;
    private readonly PrintService _printer;

    public DocumentPreviewWindow(SalesDocument document, BusinessProfile business, PrintSettings print, PrintService printer, PaperSize paper)
    {
        InitializeComponent();
        _document = document;
        _business = business;
        _print = print;
        _printer = printer;

        HeadingText.Text = string.IsNullOrEmpty(document.Number) ? document.Title : $"{document.Title}  {document.Number}";
        Title = $"WAVE POS — {HeadingText.Text}";

        PaperBox.ItemsSource = SettingsViewModel.PaperOptions;
        PaperBox.SelectedItem = SettingsViewModel.PaperOptions.First(o => o.Value == paper); // triggers first render
    }

    private PaperSize SelectedPaper => ((PaperOption)PaperBox.SelectedItem).Value;

    private void OnPaperChanged(object sender, SelectionChangedEventArgs e)
    {
        var doc = DocumentRenderer.Build(_document, _business, Logo.For(_business), SelectedPaper);
        Page.Width = doc.PageWidth;
        Viewer.Document = doc;
    }

    private void OnPrint(object sender, RoutedEventArgs e)
    {
        try
        {
            // From the preview always show the print dialog, so the user can pick a printer or PDF.
            if (_printer.Print(_document, _business, SelectedPaper, _print.PrinterName, askForPrinter: true)) Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Printing failed:\n\n{ex.Message}", "WAVE POS", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
