using WavePOS.Core.Models;

namespace WavePOS.Core.Returns;

/// <summary>A line from the original sale, with how much has already been returned.</summary>
public record ReturnableLine(
    long SaleLineId, long ProductId, string ProductName, decimal UnitPrice, decimal VatRate,
    int QuantitySold, int QuantityAlreadyReturned)
{
    public int QuantityRemaining => QuantitySold - QuantityAlreadyReturned;
}

public record ReturnLineRequest(long SaleLineId, int Quantity, bool Restock);

public enum ReturnReason
{
    WrongSize,
    Faulty,
    ChangedMind,
    NotAsDescribed,
    Exchange,
    Other,
}

public static class ReturnReasonNames
{
    public static string Label(this ReturnReason reason) => reason switch
    {
        ReturnReason.WrongSize => "Wrong size / fit",
        ReturnReason.ChangedMind => "Changed mind",
        ReturnReason.NotAsDescribed => "Not as described",
        _ => reason.ToString(),
    };
}

/// <summary>A refund for part or all of a sale. Exchanges are a refund plus a new sale.</summary>
public class ReturnRequest
{
    public long SaleId { get; init; }
    public List<ReturnLineRequest> Lines { get; init; } = [];
    public ReturnReason Reason { get; init; }
    public string Note { get; init; } = "";
    public PaymentMethod RefundMethod { get; init; }
}

public record ValidatedReturnLine(ReturnableLine Line, int Quantity, bool Restock)
{
    public decimal Amount => Line.UnitPrice * Quantity;
    public decimal Vat => Money.Round(Amount - Amount / (1 + Line.VatRate));
}

public static class ReturnCalculator
{
    /// <summary>
    /// Checks a return against what was sold: no more than the remaining quantity per line,
    /// at least one item, and only lines from this sale. Returns the lines with amounts.
    /// </summary>
    public static List<ValidatedReturnLine> Validate(ReturnRequest request, IReadOnlyList<ReturnableLine> saleLines)
    {
        var byId = saleLines.ToDictionary(l => l.SaleLineId);
        var result = new List<ValidatedReturnLine>();

        foreach (var req in request.Lines.Where(l => l.Quantity != 0))
        {
            if (!byId.TryGetValue(req.SaleLineId, out var line))
                throw new InvalidOperationException("That item isn't on this sale.");
            if (req.Quantity < 0)
                throw new InvalidOperationException("Return quantities can't be negative.");
            if (req.Quantity > line.QuantityRemaining)
                throw new InvalidOperationException(
                    $"Only {line.QuantityRemaining} × {line.ProductName} can still be returned.");
            result.Add(new ValidatedReturnLine(line, req.Quantity, req.Restock));
        }

        if (result.Count == 0) throw new InvalidOperationException("Choose at least one item to return.");
        return result;
    }

    public static decimal Total(IEnumerable<ValidatedReturnLine> lines) => lines.Sum(l => l.Amount);
    public static decimal Vat(IEnumerable<ValidatedReturnLine> lines) => lines.Sum(l => l.Vat);
}
