using System.Globalization;
using System.Text;

namespace WavePOS.Core;

/// <summary>Reading and writing CSV files (bank statements in, reports out).</summary>
public static class Csv
{
    /// <summary>
    /// Quotes a value for a CSV cell, and stops spreadsheet apps treating text that starts
    /// with = + - @ as a formula.
    /// </summary>
    public static string Escape(string value)
    {
        if (value.Length > 0 && "=+-@".Contains(value[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>A money cell: plain number with 2 decimals and no currency symbol, so spreadsheets can add it up.</summary>
    public static string Amount(decimal value) => Money.Round(value).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Picks the delimiter (comma, semicolon or tab) used most on the first lines of the file.</summary>
    public static char DetectDelimiter(string text)
    {
        var sample = string.Join('\n', text.Split('\n').Take(15));
        return new[] { ',', ';', '\t' }.OrderByDescending(d => sample.Count(c => c == d)).First();
    }

    /// <summary>Splits CSV text into rows of cells. Handles quotes, doubled quotes and line breaks inside quotes.</summary>
    public static List<List<string>> Parse(string text, char delimiter = ',')
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else inQuotes = false;
                }
                else cell.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == delimiter) { row.Add(cell.ToString()); cell.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString());
                cell.Clear();
                rows.Add(row);
                row = [];
            }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }

        // Drop blank lines.
        return rows.Where(r => r.Any(v => v.Trim().Length > 0)).ToList();
    }
}
