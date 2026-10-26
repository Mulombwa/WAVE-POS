using System.Globalization;

namespace WavePOS.Core;

/// <summary>Money helpers. Currency is Zambian Kwacha (K), 100 ngwee to the kwacha.</summary>
public static class Money
{
    public const string Symbol = "K";

    public static decimal Round(decimal amount) =>
        Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    public static string Format(decimal amount) =>
        $"{Symbol}{Round(amount).ToString("N2", CultureInfo.InvariantCulture)}";

    /// <summary>Converts to whole ngwee for exact storage in the database.</summary>
    public static long ToNgwee(decimal amount) => (long)(Round(amount) * 100);

    public static decimal FromNgwee(long ngwee) => ngwee / 100m;
}
