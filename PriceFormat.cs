using System.Globalization;

namespace LoupixDeck.Plugin.CoinGecko;

internal static class PriceFormat
{
    // Only symbols the host font is sure to have; every other currency gets its code as a suffix.
    private static readonly Dictionary<string, string> Prefixes = new()
    {
        ["usd"] = "$",
        ["jpy"] = "¥",
        ["eur"] = "€",
        ["gbp"] = "£",
        ["aud"] = "A$",
        ["cad"] = "CA$",
        ["hkd"] = "HK$",
        ["sgd"] = "S$"
    };

    /// <summary>"¥12,852,156", "$0.1234", "0.00001234 BTC" — decimals follow the price's size.</summary>
    public static string Price(decimal price, string currency)
    {
        var number = price.ToString("N" + Decimals(price), CultureInfo.InvariantCulture);
        return Prefixes.TryGetValue(currency, out var prefix)
            ? prefix + number
            : $"{number} {currency.ToUpperInvariant()}";
    }

    /// <summary>"+2.34%" / "-0.45%".</summary>
    public static string Change(decimal change)
    {
        var rounded = Math.Round(change, 2);
        var sign = rounded > 0 ? "+" : rounded < 0 ? "-" : "";
        return sign + Math.Abs(rounded).ToString("0.00", CultureInfo.InvariantCulture) + "%";
    }

    private static int Decimals(decimal price)
    {
        price = Math.Abs(price);
        if (price >= 1000) return 0;
        if (price >= 1) return 2;
        if (price >= 0.01m) return 4;
        if (price == 0) return 0;

        // Four significant digits for tiny prices: 0.00001234.
        var leadingZeros = -(int)Math.Floor(Math.Log10((double)price)) - 1;
        return Math.Min(leadingZeros + 4, 12);
    }
}
