namespace WavePOS.Core.Models;

public enum PaymentMethod
{
    Cash,
    Card,
    MobileMoney,
}

public record SaleLine(
    long ProductId,
    string ProductName,
    decimal UnitPrice,
    int Quantity,
    decimal VatRate)
{
    public decimal LineTotal => UnitPrice * Quantity;
}

/// <summary>A completed sale, ready to save and print.</summary>
public class Sale
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }

    /// <summary>The user who rang up the sale.</summary>
    public long? CashierId { get; set; }

    /// <summary>The customer, if one was attached at the till.</summary>
    public long? CustomerId { get; set; }
    public List<SaleLine> Lines { get; set; } = [];
    public decimal Total { get; set; }
    public decimal VatAmount { get; set; }
    public PaymentMethod PaymentMethod { get; set; }
    public decimal AmountTendered { get; set; }
    public decimal Change { get; set; }
}
