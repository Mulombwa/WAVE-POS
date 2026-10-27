namespace WavePOS.Core.Accounting;

/// <summary>What created a journal entry.</summary>
public enum JournalSource
{
    /// <summary>Typed in by an accountant or bookkeeper.</summary>
    Manual,
    Sale,
    Return,
    Stock,
    /// <summary>A bank or wallet statement line given an account during reconciliation.</summary>
    Bank,
    /// <summary>Balances brought in when the books were started.</summary>
    Opening,
    /// <summary>Cancels an earlier entry. Posted entries are never edited or deleted.</summary>
    Reversal,
}

public static class JournalSources
{
    public static string Label(this JournalSource source) => source switch
    {
        JournalSource.Manual => "Manual journal",
        JournalSource.Return => "Refund",
        JournalSource.Stock => "Stock",
        JournalSource.Bank => "Bank",
        JournalSource.Opening => "Opening balance",
        JournalSource.Reversal => "Reversal",
        _ => "Sale",
    };

    /// <summary>
    /// Entries the till and stock screens post automatically. To undo them, refund the sale or
    /// adjust the stock instead, so the books and the shop floor stay in step.
    /// </summary>
    public static bool IsAutomatic(this JournalSource source) =>
        source is JournalSource.Sale or JournalSource.Return or JournalSource.Stock;
}

public record JournalLineDraft(long AccountId, decimal Debit, decimal Credit, string Description = "");

/// <summary>A journal entry before it is posted.</summary>
public class JournalDraft
{
    public DateOnly Date { get; set; }
    public string Reference { get; set; } = "";
    public string Memo { get; set; } = "";
    public JournalSource Source { get; set; } = JournalSource.Manual;

    /// <summary>The sale, refund, product or bank line this came from.</summary>
    public string? SourceId { get; set; }
    public List<JournalLineDraft> Lines { get; set; } = [];

    public decimal TotalDebit => Lines.Sum(l => l.Debit);
    public decimal TotalCredit => Lines.Sum(l => l.Credit);
}

/// <summary>Double-entry rules every entry must follow before it can be posted.</summary>
public static class JournalValidator
{
    /// <summary>Returns the problem, or null if the entry can be posted.</summary>
    public static string? Check(JournalDraft entry, DateOnly? lockDate = null)
    {
        if (entry.Lines.Count < 2) return "A journal needs at least two lines.";
        foreach (var l in entry.Lines)
        {
            if (l.AccountId <= 0) return "Choose an account on every line.";
            if (l.Debit < 0 || l.Credit < 0) return "Amounts can't be negative. Use the other column instead.";
            if (l.Debit != Money.Round(l.Debit) || l.Credit != Money.Round(l.Credit)) return "Amounts can only have two decimal places.";
            if ((l.Debit > 0) == (l.Credit > 0)) return "Each line needs either a debit or a credit, not both.";
        }
        if (entry.TotalDebit != entry.TotalCredit)
            return $"Debits ({Money.Format(entry.TotalDebit)}) and credits ({Money.Format(entry.TotalCredit)}) must be equal.";
        if (lockDate is { } locked && entry.Date <= locked)
            return $"The books are locked up to {locked:dd MMM yyyy}. Use a later date, or ask a manager to change the lock date.";
        return null;
    }

    /// <summary>The same entry with debits and credits swapped, to cancel it.</summary>
    public static List<JournalLineDraft> Reverse(IEnumerable<JournalLineDraft> lines) =>
        lines.Select(l => l with { Debit = l.Credit, Credit = l.Debit }).ToList();
}
