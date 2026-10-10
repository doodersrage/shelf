using System.Globalization;

namespace Shelf.Api;

// Dates written the way the reader's region writes them: the order of day, month, and year, and the month names,
// follow the current culture, which comes from the reader's account or their browser.
public static class Dates
{
    // "Oct 9, 2026" in the US, "9 Oct 2026" in Britain, "9. Okt. 2026" in Germany, "2026 10月 9" in Japan.
    public static string Medium(this DateOnly date) => date.ToString(Pattern("MMM"), CultureInfo.CurrentCulture);

    public static string Medium(this DateTimeOffset moment) => moment.ToString(Pattern("MMM"), CultureInfo.CurrentCulture);

    public static string Long(this DateOnly date) => date.ToString(Pattern("MMMM"), CultureInfo.CurrentCulture);

    public static string MonthDay(this DateOnly date) => date.ToString(MonthDayPattern(), CultureInfo.CurrentCulture);

    public static string MonthDay(this DateTimeOffset moment) => moment.ToString(MonthDayPattern(), CultureInfo.CurrentCulture);

    public static string MonthYear(this DateOnly date) => date.ToString("Y", CultureInfo.CurrentCulture);

    public static string FullMonthDay(this DateOnly date) => date.ToString("M", CultureInfo.CurrentCulture);

    public static string Stamp(this DateTimeOffset moment) =>
        $"{moment.Medium()} {moment.ToString("t", CultureInfo.CurrentCulture)}";

    public static string ShortStamp(this DateTimeOffset moment) =>
        $"{moment.MonthDay()} {moment.ToString("t", CultureInfo.CurrentCulture)}";

    // The order of a region's numeric dates decides the order of the written one.
    private static Order Ordering()
    {
        var pattern = CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern.TrimStart();
        return pattern.StartsWith('y') ? Order.YearFirst : pattern.StartsWith('M') ? Order.MonthFirst : Order.DayFirst;
    }

    private static string Pattern(string month) => Ordering() switch
    {
        Order.MonthFirst => $"{month} d, yyyy",
        Order.YearFirst => $"yyyy {month} d",
        _ => CultureInfo.CurrentCulture.TwoLetterISOLanguageName == "de" ? $"d. {month} yyyy" : $"d {month} yyyy",
    };

    private static string MonthDayPattern() => Ordering() switch
    {
        Order.MonthFirst => "MMM d",
        Order.YearFirst => "MMM d",
        _ => CultureInfo.CurrentCulture.TwoLetterISOLanguageName == "de" ? "d. MMM" : "d MMM",
    };

    private enum Order
    {
        MonthFirst,
        DayFirst,
        YearFirst,
    }
}
