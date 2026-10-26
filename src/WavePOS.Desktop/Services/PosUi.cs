using System.Windows;
using WavePOS.Core.Documents;
using WavePOS.Core.Models;
using WavePOS.Data;
using WavePOS.Desktop.Printing;
using WavePOS.Desktop.ViewModels;

namespace WavePOS.Desktop.Services;

/// <summary>Opens dialogs and prints on behalf of view models, so they don't create windows themselves.</summary>
public class PosUi(PosDatabase db, SettingsRepository settings, PrintService printer)
{
    private static Window? Owner =>
        Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;

    /// <summary>Asks for a line of text. Returns null if cancelled.</summary>
    public string? AskText(string title, string prompt, string confirmText)
    {
        var window = new TextPromptWindow(title, prompt, confirmText) { Owner = Owner };
        return window.ShowDialog() == true ? window.Value : null;
    }

    public void Preview(SalesDocument document)
    {
        var print = settings.GetPrintSettings();
        var paper = document.Kind == DocumentKind.Receipt ? print.ReceiptPaper : print.InvoicePaper;
        new DocumentPreviewWindow(document, settings.GetBusinessProfile(), print, printer, paper) { Owner = Owner }.ShowDialog();
    }

    /// <summary>Prints straight to the saved printer (asks only if none is set). Returns false if cancelled.</summary>
    public bool PrintReceipt(SalesDocument receipt)
    {
        var print = settings.GetPrintSettings();
        return printer.Print(receipt, settings.GetBusinessProfile(), print.ReceiptPaper, print.PrinterName, askForPrinter: false);
    }

    /// <summary>Opens Settings. Returns true if the user saved changes.</summary>
    public bool EditSettings() =>
        new SettingsWindow(new SettingsViewModel(settings, db.DataDirectory)) { Owner = Owner }.ShowDialog() == true;
}
