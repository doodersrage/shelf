using System.Globalization;

namespace Shelf.Api.Books;

// Amounts paid and got, in the shelf's one currency: Shelf:Currency as an ISO code ("EUR"), or else the
// currency of the language a reader reads Shelf in.
public static class Money
{
    private static string? _symbol;

    public static string? Currency { get; private set; }

    public static void Use(string? currency)
    {
        Currency = string.IsNullOrWhiteSpace(currency) ? null : currency.Trim().ToUpperInvariant();
        _symbol = null;
    }

    public static string Show(decimal amount) => amount.ToString("C", Format());

    private static NumberFormatInfo Format()
    {
        var culture = CultureInfo.CurrentCulture;
        if (Currency is null)
        {
            return culture.NumberFormat;
        }

        var format = (NumberFormatInfo)culture.NumberFormat.Clone();
        format.CurrencySymbol = _symbol ??= Symbol(Currency);
        return format;
    }

    private static string Symbol(string code)
    {
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var region = new RegionInfo(culture.Name);
                if (region.ISOCurrencySymbol == code)
                {
                    return region.CurrencySymbol;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return code;
    }
}
