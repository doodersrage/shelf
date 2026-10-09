using System.ComponentModel.DataAnnotations;
using Shelf.Api.Books;

namespace Shelf.Api.Components.Pages;

public sealed class BookFormModel
{
    [Required, MaxLength(200)]
    public string Title { get; set; } = "";

    [MaxLength(200)]
    public string Subtitle { get; set; } = "";

    [Required, MaxLength(200)]
    public string Author { get; set; } = "";

    public BookStatus Status { get; set; } = BookStatus.Want;

    [Range(1, 5)]
    public int? Rating { get; set; }

    [Range(1000, 2100)]
    public int? Year { get; set; }

    [MaxLength(32)]
    public string Isbn { get; set; } = "";

    [Range(1, 20000)]
    public int? Pages { get; set; }

    [Range(0, 20000)]
    public int? CurrentPage { get; set; }

    [MaxLength(4000)]
    public string Notes { get; set; } = "";

    public DateOnly? StartedOn { get; set; }

    public DateOnly? FinishedOn { get; set; }

    [MaxLength(120)]
    public string LoanedTo { get; set; } = "";

    public DateOnly? LoanedOn { get; set; }

    [MaxLength(200)]
    public string Publisher { get; set; } = "";

    [MaxLength(40)]
    public string Language { get; set; } = "";

    public string Format { get; set; } = "";

    [MaxLength(200)]
    public string Series { get; set; } = "";

    [Range(1, 999)]
    public int? SeriesNumber { get; set; }

    [MaxLength(500)]
    public string CoverUrl { get; set; } = "";

    [MaxLength(4000)]
    public string Review { get; set; } = "";

    public bool Loved { get; set; }

    [MaxLength(80)]
    public string Location { get; set; } = "";

    public DateOnly? AcquiredOn { get; set; }

    [MaxLength(200)]
    public string OriginalTitle { get; set; } = "";

    [MaxLength(200)]
    public string Translator { get; set; } = "";

    [MaxLength(120)]
    public string RecommendedBy { get; set; } = "";

    public string Tags { get; set; } = "";

    public BookWrite ToWrite() => BookWrite.From(
        Title,
        Author,
        Status,
        Rating,
        Year,
        Isbn,
        Pages,
        CurrentPage,
        Notes,
        StartedOn,
        FinishedOn,
        LoanedTo,
        string.IsNullOrWhiteSpace(Tags)
            ? []
            : Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
        Subtitle,
        Publisher,
        Language,
        Enum.TryParse<BookFormat>(Format, out var format) ? format : null,
        Series,
        SeriesNumber,
        CoverUrl,
        Review,
        Loved,
        LoanedOn,
        Location,
        AcquiredOn,
        Translator,
        RecommendedBy,
        OriginalTitle);

    public bool FillBlanks(CatalogMatch match)
    {
        var filled = false;
        filled |= Assign(Title, match.Title, value => Title = value, 200);
        filled |= Assign(Author, match.Author, value => Author = value, 200);
        filled |= Assign(Publisher, match.Publisher, value => Publisher = value, 200);
        filled |= Assign(Language, match.Language, value => Language = value, 40);
        filled |= Assign(CoverUrl, match.CoverUrl, value => CoverUrl = value, 500);
        if (Year is null && match.Year is >= 1000 and <= 2100)
        {
            Year = match.Year;
            filled = true;
        }

        if (Pages is null && match.Pages is >= 1 and <= 20000)
        {
            Pages = match.Pages;
            filled = true;
        }

        return filled;
    }

    private static bool Assign(string current, string? incoming, Action<string> set, int max)
    {
        if (!string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(incoming))
        {
            return false;
        }

        var trimmed = incoming.Trim();
        set(trimmed.Length <= max ? trimmed : trimmed[..max]);
        return true;
    }

    public void Clear()
    {
        Title = "";
        Subtitle = "";
        Author = "";
        Status = BookStatus.Want;
        Rating = null;
        Year = null;
        Isbn = "";
        Pages = null;
        CurrentPage = null;
        Notes = "";
        StartedOn = null;
        FinishedOn = null;
        LoanedTo = "";
        LoanedOn = null;
        Publisher = "";
        Language = "";
        Format = "";
        Series = "";
        SeriesNumber = null;
        CoverUrl = "";
        Review = "";
        Loved = false;
        Location = "";
        AcquiredOn = null;
        Translator = "";
        OriginalTitle = "";
        RecommendedBy = "";
        Tags = "";
    }

    public static BookFormModel From(Book book) => new()
    {
        Title = book.Title,
        Subtitle = book.Subtitle ?? "",
        Author = book.Author,
        Status = book.Status,
        Rating = book.Rating,
        Year = book.Year,
        Isbn = book.Isbn ?? "",
        Pages = book.Pages,
        CurrentPage = book.CurrentPage,
        Notes = book.Notes ?? "",
        StartedOn = book.StartedOn,
        FinishedOn = book.FinishedOn,
        LoanedTo = book.LoanedTo ?? "",
        LoanedOn = book.LoanedOn,
        Publisher = book.Publisher ?? "",
        Language = book.Language ?? "",
        Format = book.Format?.ToString() ?? "",
        Series = book.Series ?? "",
        SeriesNumber = book.SeriesNumber,
        CoverUrl = book.CoverUrl ?? "",
        Review = book.Review ?? "",
        Loved = book.Loved,
        Location = book.Location ?? "",
        AcquiredOn = book.AcquiredOn,
        Translator = book.Translator ?? "",
        OriginalTitle = book.OriginalTitle ?? "",
        RecommendedBy = book.RecommendedBy ?? "",
        Tags = string.Join(", ", book.Tags.Select(tag => tag.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)),
    };
}

public sealed class SessionFormModel
{
    public DateOnly? Date { get; set; }

    [Range(0, 20000)]
    public int? FromPage { get; set; }

    [Range(0, 20000)]
    public int? ToPage { get; set; }

    [MaxLength(500)]
    public string Note { get; set; } = "";

    public void Clear()
    {
        Date = null;
        FromPage = null;
        ToPage = null;
        Note = "";
    }
}

public sealed class GoalFormModel
{
    [Range(0, 1000)]
    public int YearlyGoal { get; set; }
}

public sealed class QuoteFormModel
{
    [Required, MaxLength(1000)]
    public string Text { get; set; } = "";

    [Range(1, 20000)]
    public int? Page { get; set; }

    public void Clear()
    {
        Text = "";
        Page = null;
    }
}
