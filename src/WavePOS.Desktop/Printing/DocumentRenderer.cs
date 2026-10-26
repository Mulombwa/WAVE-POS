using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using WavePOS.Core;
using WavePOS.Core.Documents;
using WavePOS.Core.Models;

namespace WavePOS.Desktop.Printing;

/// <summary>
/// Lays out receipts and invoices as WPF FlowDocuments, either on an 80mm receipt roll or an A4 page.
/// A fresh document is built for every preview or print (a FlowDocument can only be shown in one place).
/// </summary>
public static class DocumentRenderer
{
    // WPF units are 1/96 inch.
    public const double Receipt80mmWidth = 80 / 25.4 * 96;   // ≈ 302
    public const double A4Width = 210 / 25.4 * 96;           // ≈ 794
    public const double A4Height = 297 / 25.4 * 96;          // ≈ 1123

    private static readonly Brush Ink = Frozen("#231F20");
    private static readonly Brush Muted = Frozen("#6B7385");
    private static readonly Brush BrandBlue = Frozen("#4A6FB0");
    private static readonly Brush Line = Frozen("#C9D1DE");
    private static readonly Brush HeaderFill = Frozen("#EAF0F9");
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <param name="pageWidth">Override the page width (e.g. the printer's printable width).</param>
    /// <param name="pageHeight">Override the page height (e.g. the printer's printable height).</param>
    public static FlowDocument Build(SalesDocument d, BusinessProfile business, ImageSource logo, PaperSize paper,
        double? pageWidth = null, double? pageHeight = null) =>
        paper == PaperSize.A4
            ? BuildA4(d, business, logo, pageWidth ?? A4Width, pageHeight ?? A4Height)
            : BuildReceipt(d, business, logo, pageWidth ?? Receipt80mmWidth, pageHeight ?? double.NaN);

    public static double PageWidth(PaperSize paper) => paper == PaperSize.A4 ? A4Width : Receipt80mmWidth;

    // ---------- 80mm receipt roll ----------

    private static FlowDocument BuildReceipt(SalesDocument d, BusinessProfile b, ImageSource logo, double width, double height)
    {
        var padding = new Thickness(10, 12, 10, 16);
        var doc = NewDocument(width, height, padding, 11);
        var content = ContentWidth(width, padding);

        doc.Blocks.Add(LogoBlock(logo, maxWidth: 190, maxHeight: 70, HorizontalAlignment.Center));
        var head = Para(TextAlignment.Center, new Thickness(0, 6, 0, 0));
        AddLine(head, b.Name, bold: true, size: 13);
        foreach (var detail in BusinessDetails(b)) AddLine(head, detail, color: Muted);
        doc.Blocks.Add(head);

        doc.Blocks.Add(Rule());
        doc.Blocks.Add(Para(TextAlignment.Center, new Thickness(0, 2, 0, 4), Run(d.Title, bold: true, size: 13)));

        var meta = TwoColumnTable(content, fixedWidth: content * 0.55);
        AddPair(meta, d.Kind == DocumentKind.Receipt ? "Receipt no." : "Invoice no.", d.Number);
        AddPair(meta, "Date", d.Date.ToString("dd MMM yyyy HH:mm", Culture));
        AddPair(meta, "Served by", d.CashierName);
        if (d.CustomerName is { } customer) AddPair(meta, "Customer", customer);
        if (d.ValidUntil is { } valid) AddPair(meta, "Valid until", valid.ToString("dd MMM yyyy", Culture));
        doc.Blocks.Add(meta);

        doc.Blocks.Add(Rule());

        var items = TwoColumnTable(content, fixedWidth: 80);
        foreach (var line in d.Lines)
        {
            AddPair(items, line.Description, Money.Format(line.LineTotal), leftBold: true);
            AddPair(items, $"  {line.Quantity} × {Money.Format(line.UnitPrice)}{(line.VatRate > 0 ? "" : "  (VAT exempt)")}", "", muted: true);
        }
        doc.Blocks.Add(items);

        doc.Blocks.Add(Rule());

        var totals = TwoColumnTable(content, fixedWidth: 110);
        AddPair(totals, "TOTAL", Money.Format(d.Total), leftBold: true, rightBold: true, size: 14);
        AddPair(totals, "VAT included", Money.Format(d.VatAmount), muted: true);
        if (d.PaymentMethod is { } method)
        {
            AddPair(totals, $"Paid ({method.DisplayName()})", Money.Format(d.AmountTendered));
            if (method == PaymentMethod.Cash) AddPair(totals, "Change", Money.Format(d.Change), rightBold: true);
        }
        doc.Blocks.Add(totals);

        if (d.Kind == DocumentKind.DraftInvoice)
            doc.Blocks.Add(Para(TextAlignment.Center, new Thickness(0, 10, 0, 0),
                Run("Draft invoice — not a receipt. Prices valid until the date above.", italic: true, color: Muted, size: 10)));

        if (!string.IsNullOrWhiteSpace(b.ReceiptFooter))
            doc.Blocks.Add(Para(TextAlignment.Center, new Thickness(0, 12, 0, 0), Run(b.ReceiptFooter, bold: true)));

        return doc;
    }

    // ---------- A4 page ----------

    private static FlowDocument BuildA4(SalesDocument d, BusinessProfile b, ImageSource logo, double width, double height)
    {
        var padding = new Thickness(56, 48, 56, 48);
        var doc = NewDocument(width, height, padding, 12);
        var content = ContentWidth(width, padding);

        // Header: logo + business on the left, title + numbers on the right.
        var header = new Table { CellSpacing = 0, Margin = new Thickness(0) };
        header.Columns.Add(Column(content - 260));
        header.Columns.Add(Column(260));
        var group = new TableRowGroup();
        var row = new TableRow();

        var left = new TableCell();
        left.Blocks.Add(LogoBlock(logo, maxWidth: 240, maxHeight: 90, HorizontalAlignment.Left));
        var biz = Para(TextAlignment.Left, new Thickness(0, 8, 0, 0));
        AddLine(biz, b.Name, bold: true, size: 14);
        foreach (var detail in BusinessDetails(b)) AddLine(biz, detail, color: Muted);
        left.Blocks.Add(biz);

        var right = new TableCell();
        right.Blocks.Add(Para(TextAlignment.Right, new Thickness(0), Run(d.Title, bold: true, size: 26, color: BrandBlue)));
        var nums = TwoColumnTable(260, fixedWidth: 130, rightAlignLabels: true);
        AddPair(nums, d.Kind == DocumentKind.Receipt ? "Receipt no." : "Invoice no.", d.Number, rightBold: true);
        AddPair(nums, "Date", d.Date.ToString("dd MMM yyyy", Culture));
        if (d.ValidUntil is { } valid) AddPair(nums, "Valid until", valid.ToString("dd MMM yyyy", Culture));
        AddPair(nums, "Prepared by", d.CashierName);
        right.Blocks.Add(nums);

        row.Cells.Add(left);
        row.Cells.Add(right);
        group.Rows.Add(row);
        header.RowGroups.Add(group);
        doc.Blocks.Add(header);

        // Bill to
        var billTo = Para(TextAlignment.Left, new Thickness(0, 24, 0, 16));
        AddLine(billTo, d.Kind == DocumentKind.Receipt ? "CUSTOMER" : "BILL TO", bold: true, color: Muted, size: 10);
        AddLine(billTo, d.CustomerName ?? "—", bold: true, size: 14);
        doc.Blocks.Add(billTo);

        // Items
        var items = new Table { CellSpacing = 0 };
        foreach (var w in new[] { content - (60 + 110 + 70 + 120), 60, 110, 70, 120 })
            items.Columns.Add(Column(w));

        var rows = new TableRowGroup();
        var headRow = new TableRow { Background = HeaderFill };
        foreach (var (text, align) in new[] { ("Description", TextAlignment.Left), ("Qty", TextAlignment.Right),
                     ("Unit price", TextAlignment.Right), ("VAT", TextAlignment.Right), ("Amount", TextAlignment.Right) })
            headRow.Cells.Add(Cell(text, align, bold: true, padding: new Thickness(8, 7, 8, 7)));
        rows.Rows.Add(headRow);

        foreach (var line in d.Lines)
        {
            var r = new TableRow();
            r.Cells.Add(Cell(line.Description, TextAlignment.Left, bottomBorder: true));
            r.Cells.Add(Cell(line.Quantity.ToString(Culture), TextAlignment.Right, bottomBorder: true));
            r.Cells.Add(Cell(Money.Format(line.UnitPrice), TextAlignment.Right, bottomBorder: true));
            r.Cells.Add(Cell(line.VatRate > 0 ? $"{line.VatRate * 100:0.#}%" : "Exempt", TextAlignment.Right, bottomBorder: true));
            r.Cells.Add(Cell(Money.Format(line.LineTotal), TextAlignment.Right, bottomBorder: true));
            rows.Rows.Add(r);
        }
        items.RowGroups.Add(rows);
        doc.Blocks.Add(items);

        // Totals, right-aligned
        var totals = new Table { CellSpacing = 0, Margin = new Thickness(0, 12, 0, 0) };
        totals.Columns.Add(Column(content - 290));
        totals.Columns.Add(Column(170));
        totals.Columns.Add(Column(120));
        var trows = new TableRowGroup();
        void TotalRow(string label, string value, bool strong = false)
        {
            var r = new TableRow();
            r.Cells.Add(new TableCell());
            r.Cells.Add(Cell(label, TextAlignment.Right, bold: strong, size: strong ? 15 : 12));
            r.Cells.Add(Cell(value, TextAlignment.Right, bold: strong, size: strong ? 15 : 12));
            trows.Rows.Add(r);
        }
        TotalRow("Subtotal (excl. VAT)", Money.Format(d.NetAmount));
        TotalRow("VAT", Money.Format(d.VatAmount));
        TotalRow("Total", Money.Format(d.Total), strong: true);
        if (d.PaymentMethod is { } method)
        {
            TotalRow($"Paid ({method.DisplayName()})", Money.Format(d.AmountTendered));
            if (method == PaymentMethod.Cash) TotalRow("Change", Money.Format(d.Change));
        }
        totals.RowGroups.Add(trows);
        doc.Blocks.Add(totals);

        if (d.Kind == DocumentKind.DraftInvoice)
            doc.Blocks.Add(Para(TextAlignment.Left, new Thickness(0, 28, 0, 0),
                Run($"This is a draft invoice (quotation), not a tax invoice or receipt. Prices are valid until {d.ValidUntil:dd MMM yyyy}.",
                    italic: true, color: Muted)));

        if (!string.IsNullOrWhiteSpace(b.ReceiptFooter))
            doc.Blocks.Add(Para(TextAlignment.Center, new Thickness(0, 28, 0, 0), Run(b.ReceiptFooter, bold: true)));

        return doc;
    }

    // ---------- helpers ----------

    private static FlowDocument NewDocument(double width, double height, Thickness padding, double fontSize) => new()
    {
        PageWidth = width,
        PageHeight = height,
        ColumnWidth = width, // one column
        PagePadding = padding,
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = fontSize,
        Foreground = Ink,
        Background = Brushes.White,
        TextAlignment = TextAlignment.Left,
    };

    private static IEnumerable<string> BusinessDetails(BusinessProfile b)
    {
        foreach (var line in b.Address.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) yield return line;
        var contact = string.Join("  ·  ", new[] { b.Phone, b.Email }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (contact.Length > 0) yield return contact;
        if (!string.IsNullOrWhiteSpace(b.Tpin)) yield return $"TPIN: {b.Tpin}";
    }

    private static Block LogoBlock(ImageSource logo, double maxWidth, double maxHeight, HorizontalAlignment align) =>
        new BlockUIContainer(new Image
        {
            Source = logo,
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = align,
        });

    private static Block Rule() => new BlockUIContainer(new Rectangle
    {
        Height = 1,
        Fill = Line,
        Margin = new Thickness(0, 8, 0, 8),
        HorizontalAlignment = HorizontalAlignment.Stretch,
    });

    private static Paragraph Para(TextAlignment align, Thickness margin, params Inline[] inlines)
    {
        var p = new Paragraph { TextAlignment = align, Margin = margin };
        p.Inlines.AddRange(inlines);
        return p;
    }

    private static Run Run(string text, bool bold = false, bool italic = false, double? size = null, Brush? color = null)
    {
        var r = new Run(text);
        if (bold) r.FontWeight = FontWeights.SemiBold;
        if (italic) r.FontStyle = FontStyles.Italic;
        if (size is { } s) r.FontSize = s;
        if (color is not null) r.Foreground = color;
        return r;
    }

    private static void AddLine(Paragraph p, string text, bool bold = false, double? size = null, Brush? color = null)
    {
        if (p.Inlines.Count > 0) p.Inlines.Add(new LineBreak());
        p.Inlines.Add(Run(text, bold: bold, size: size, color: color));
    }

    // Tables use exact column widths: "fill remaining space" (star) columns collapse inside FlowDocuments.
    private static double ContentWidth(double pageWidth, Thickness padding) => pageWidth - padding.Left - padding.Right;

    private static TableColumn Column(double width) => new() { Width = new GridLength(Math.Max(width, 20)) };

    private static Table TwoColumnTable(double totalWidth, double fixedWidth, bool rightAlignLabels = false)
    {
        var t = new Table { CellSpacing = 0, Margin = new Thickness(0), Tag = rightAlignLabels };
        t.Columns.Add(Column(totalWidth - fixedWidth));
        t.Columns.Add(Column(fixedWidth));
        t.RowGroups.Add(new TableRowGroup());
        return t;
    }

    private static void AddPair(Table t, string label, string value, bool leftBold = false, bool rightBold = false,
        bool muted = false, double? size = null)
    {
        var rightAlignLabels = t.Tag is true;
        var row = new TableRow();
        row.Cells.Add(Cell(label, rightAlignLabels ? TextAlignment.Right : TextAlignment.Left, leftBold, size,
            muted || rightAlignLabels ? Muted : null, new Thickness(0, 1, rightAlignLabels ? 10 : 4, 1)));
        row.Cells.Add(Cell(value, TextAlignment.Right, rightBold, size, muted ? Muted : null, new Thickness(0, 1, 0, 1)));
        t.RowGroups[0].Rows.Add(row);
    }

    private static TableCell Cell(string text, TextAlignment align, bool bold = false, double? size = null,
        Brush? color = null, Thickness? padding = null, bool bottomBorder = false)
    {
        var cell = new TableCell(Para(align, new Thickness(0), Run(text, bold: bold, size: size, color: color)))
        {
            Padding = padding ?? new Thickness(8, 6, 8, 6),
        };
        if (bottomBorder)
        {
            cell.BorderBrush = Line;
            cell.BorderThickness = new Thickness(0, 0, 0, 1);
        }
        return cell;
    }

    private static Brush Frozen(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
