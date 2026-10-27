using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WavePOS.Core.Accounting;

/// <summary>One line from a bank or mobile-money statement. Positive = money in, negative = money out.</summary>
public record StatementLine(DateOnly Date, string Description, decimal Amount, string Reference = "", decimal? Balance = null);

public record StatementParseResult(List<StatementLine> Lines, int SkippedRows, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>
/// Reads bank statement CSV exports. Zambian banks and wallets lay their files out differently,
/// so columns are found by their headings (Date, Description/Narration/Details,
/// Amount or Debit + Credit, Reference, Balance). Dates are read day-first (27/10/2026).
/// </summary>
public static class BankStatementParser
{
    private static readonly string[] DateHeads = ["transaction date", "trans date", "txn date", "posting date", "post date", "date"];
    private static readonly string[] DescriptionHeads = ["description", "narration", "narrative", "details", "particulars", "transaction details", "memo", "remarks", "payee"];
    private static readonly string[] ReferenceHeads = ["reference", "ref", "ref no", "reference number", "transaction id", "cheque no", "cheque number", "receipt no"];
    private static readonly string[] AmountHeads = ["amount", "transaction amount", "value", "amount zmw", "amount k"];
    private static readonly string[] DebitHeads = ["debit", "debits", "withdrawal", "withdrawals", "money out", "paid out", "dr", "debit amount", "out"];
    private static readonly string[] CreditHeads = ["credit", "credits", "deposit", "deposits", "money in", "paid in", "cr", "credit amount", "in"];
    private static readonly string[] BalanceHeads = ["balance", "running balance", "closing balance", "available balance"];

    private static readonly string[] DateFormats =
    [
        "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy", "yyyy-MM-dd", "yyyy/MM/dd",
        "dd MMM yyyy", "d MMM yyyy", "dd-MMM-yyyy", "d-MMM-yyyy", "dd-MMM-yy", "d-MMM-yy", "dd MMM yy",
        "dd/MM/yy", "d/M/yy", "dd MMMM yyyy", "d MMMM yyyy", "yyyyMMdd",
    ];

    public static StatementParseResult Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new([], 0, "The file is empty.");
        var rows = Csv.Parse(text.TrimStart('﻿'), Csv.DetectDelimiter(text));

        // The heading row is the first one (of the first 25) with a date column and an amount column.
        for (var h = 0; h < Math.Min(rows.Count, 25); h++)
        {
            var heads = rows[h].Select(Normalise).ToList();
            var date = Find(heads, DateHeads, partial: true);
            var amount = Find(heads, AmountHeads);
            var debit = Find(heads, DebitHeads);
            var credit = Find(heads, CreditHeads);
            if (date < 0 || (amount < 0 && (debit < 0 || credit < 0))) continue;

            var map = new Columns(date, Find(heads, DescriptionHeads, partial: true), Find(heads, ReferenceHeads),
                amount, debit, credit, Find(heads, BalanceHeads, partial: true));
            return ReadRows(rows.Skip(h + 1), map);
        }
        return new([], 0, "Couldn't find the column headings. The file needs a Date column and either an Amount column or Debit and Credit columns.");
    }

    private record Columns(int Date, int Description, int Reference, int Amount, int Debit, int Credit, int Balance);

    private static StatementParseResult ReadRows(IEnumerable<List<string>> rows, Columns c)
    {
        var lines = new List<StatementLine>();
        var skipped = 0;
        foreach (var row in rows)
        {
            string Cell(int i) => i >= 0 && i < row.Count ? row[i].Trim() : "";

            // Opening/closing balance rows, totals and footers have no usable date or amount: skip them.
            if (ParseDate(Cell(c.Date)) is not { } date) { skipped++; continue; }

            decimal? amount;
            if (c.Amount >= 0) amount = ParseAmount(Cell(c.Amount));
            else
            {
                var dr = ParseAmount(Cell(c.Debit));
                var cr = ParseAmount(Cell(c.Credit));
                amount = dr is null && cr is null ? null : Math.Abs(cr ?? 0) - Math.Abs(dr ?? 0);
            }
            if (amount is not { } value || value == 0) { skipped++; continue; }

            var description = Cell(c.Description);
            lines.Add(new StatementLine(date, description.Length == 0 ? "(no description)" : description, Money.Round(value),
                Cell(c.Reference), ParseAmount(Cell(c.Balance))));
        }
        return lines.Count == 0
            ? new([], skipped, "No transactions found under the headings. Check the file is a statement export.")
            : new(lines, skipped, null);
    }

    public static DateOnly? ParseDate(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        // Drop a time of day: "27/10/2026 14:05", "2026-10-27T14:05:00".
        var t = text.IndexOf('T');
        if (t == 10 && char.IsDigit(text[0])) text = text[..10];
        var space = text.LastIndexOf(' ');
        if (space > 0 && text[(space + 1)..].Contains(':')) text = text[..space].Trim();

        return DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d)
            ? d : null;
    }

    /// <summary>Reads "K1,234.50", "ZMW -20", "(150.00)", "300.00 CR", "45.00 DR". Null if blank or not a number.</summary>
    public static decimal? ParseAmount(string text)
    {
        var s = text.Trim().ToUpperInvariant();
        if (s.Length == 0 || s == "-") return null;
        var negative = false;
        if (s.EndsWith("DR")) { negative = true; s = s[..^2]; }
        else if (s.EndsWith("CR")) s = s[..^2];
        if (s.StartsWith('(') && s.EndsWith(')')) { negative = true; s = s[1..^1]; }
        s = s.Replace("ZMW", "").Replace("ZK", "").Replace("K", "").Replace(",", "").Replace(" ", "").Replace(" ", "");
        if (s.StartsWith('-')) { negative = !negative; s = s[1..]; }
        else if (s.StartsWith('+')) s = s[1..];
        if (s.EndsWith('-')) { negative = !negative; s = s[..^1]; }
        if (!decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return null;
        return negative ? -value : value;
    }

    /// <summary>
    /// A fingerprint per line so importing the same statement twice doesn't duplicate anything.
    /// Identical lines on the same day (two K20 airtime purchases) get different fingerprints by their order.
    /// </summary>
    public static List<string> Fingerprints(IReadOnlyList<StatementLine> lines)
    {
        var seen = new Dictionary<string, int>();
        var result = new List<string>(lines.Count);
        foreach (var l in lines)
        {
            var key = string.Join('|', l.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Money.ToNgwee(l.Amount).ToString(CultureInfo.InvariantCulture),
                Normalise(l.Description), Normalise(l.Reference));
            var n = seen[key] = seen.GetValueOrDefault(key) + 1;
            result.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{key}#{n}")))[..32].ToLowerInvariant());
        }
        return result;
    }

    private static int Find(List<string> heads, string[] names, bool partial = false)
    {
        foreach (var name in names)
        {
            var i = heads.IndexOf(name);
            if (i >= 0) return i;
        }
        if (!partial) return -1;
        foreach (var name in names)
        {
            var i = heads.FindIndex(h => h.Contains(name) && !h.Contains("value date"));
            if (i >= 0) return i;
        }
        return -1;
    }

    private static string Normalise(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>A ledger entry on the bank account that a statement line might be.</summary>
public record LedgerCandidate(long EntryId, DateOnly Date, decimal Amount, string Reference, string Memo);

public static class BankMatcher
{
    public const int MaxDaysApart = 7;

    /// <summary>
    /// The ledger entry most likely to be this statement line: same amount, closest date,
    /// within a week. Null if there isn't one.
    /// </summary>
    public static LedgerCandidate? Suggest(DateOnly date, decimal amount, IEnumerable<LedgerCandidate> candidates) =>
        candidates
            .Where(c => c.Amount == amount && Math.Abs(c.Date.DayNumber - date.DayNumber) <= MaxDaysApart)
            .OrderBy(c => Math.Abs(c.Date.DayNumber - date.DayNumber))
            .ThenBy(c => c.EntryId)
            .FirstOrDefault();
}
