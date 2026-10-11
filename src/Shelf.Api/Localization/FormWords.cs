using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Localization;

// Form messages and field labels for validation attributes, which read them through these properties each time,
// so they come out in the reader's language. A message leads with the field's label, which fits every language.
public static class FormWords
{
    public static string Required => T("{0}: fill this in.");
    public static string TooLong => T("{0}: keep it to {1} characters.");
    public static string OutOfRange => T("{0}: use a number from {1} to {2}.");

    public static string Title => T("Title");
    public static string Subtitle => T("Subtitle");
    public static string Author => T("Author");
    public static string Rating => T("Rating");
    public static string Year => T("Year");
    public static string Isbn => T("ISBN");
    public static string Pages => T("Pages");
    public static string CurrentPage => T("Current page");
    public static string Notes => T("Notes");
    public static string LoanedTo => T("Loaned to");
    public static string Publisher => T("Publisher");
    public static string Language => T("Language");
    public static string Series => T("Series");
    public static string SeriesNumber => T("Number in the series");
    public static string CoverUrl => T("Cover address");
    public static string Review => T("Review");
    public static string Location => T("Where it sits");
    public static string OriginalTitle => T("Original title");
    public static string Inscription => T("Inscription");
    public static string Translator => T("Translator");
    public static string Narrator => T("Narrator");
    public static string PagesGoal => T("Pages to read this year");
    public static string HoursGoal => T("Hours to spend reading and listening");
    public static string RecommendedBy => T("Recommended by");
    public static string FromPage => T("From page");
    public static string ToPage => T("To page");
    public static string Note => T("Note");
    public static string YearlyGoal => T("Books this year");
    public static string Text => T("Quote");
    public static string Page => T("Page");
}
