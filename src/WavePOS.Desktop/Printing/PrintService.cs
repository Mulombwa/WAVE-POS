using System.Printing;
using System.Windows.Controls;
using System.Windows.Documents;
using WavePOS.Core.Documents;
using WavePOS.Core.Models;
using WavePOS.Desktop.Branding;

namespace WavePOS.Desktop.Printing;

public class PrintService
{
    /// <summary>Names of printers installed on this computer (empty if the print spooler isn't running).</summary>
    public static List<string> InstalledPrinters()
    {
        try
        {
            using var server = new LocalPrintServer();
            return server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections])
                .Select(q => q.FullName).OrderBy(n => n).ToList();
        }
        catch (Exception ex) when (ex is PrintSystemException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>
    /// Prints a receipt or invoice. Uses the saved printer without asking, unless none is set,
    /// it can't be found, or <paramref name="askForPrinter"/> is true — then the print dialog is shown.
    /// Returns false if the user cancelled.
    /// </summary>
    public bool Print(SalesDocument document, BusinessProfile business, PaperSize paper, string? printerName, bool askForPrinter)
    {
        var dialog = new PrintDialog { UserPageRangeEnabled = false };
        var ask = askForPrinter || string.IsNullOrWhiteSpace(printerName);

        if (!ask)
        {
            try
            {
                dialog.PrintQueue = new PrintQueue(new LocalPrintServer(), printerName);
            }
            catch (Exception ex) when (ex is PrintSystemException or PrintQueueException or InvalidOperationException)
            {
                ask = true; // saved printer missing: let the user pick one
            }
        }

        if (ask && dialog.ShowDialog() != true) return false;

        // Lay out for the printer's actual printable area so nothing is cut off.
        var width = DocumentRenderer.PageWidth(paper);
        if (dialog.PrintableAreaWidth > 0) width = Math.Min(width, dialog.PrintableAreaWidth);
        double? height = dialog.PrintableAreaHeight > 0 ? dialog.PrintableAreaHeight : null;

        var doc = DocumentRenderer.Build(document, business, Logo.For(business), paper, width, height);
        dialog.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, $"WAVE POS {document.Title} {document.Number}");
        return true;
    }
}
