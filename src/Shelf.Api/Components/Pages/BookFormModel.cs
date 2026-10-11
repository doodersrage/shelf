using System.ComponentModel.DataAnnotations;
using Shelf.Api.Books;
using Shelf.Api.Localization;

namespace Shelf.Api.Components.Pages;

public sealed class BookFormModel
{
    [Required(ErrorMessageResourceType = typeof(FormWords), ErrorMessageResourceName = nameof(FormWords.Required)), MaxLength(200, ErrorMessageResourceType = typeof(FormWords), ErrorMessageResourceName = nameof(FormWords.TooLong)), Display(Name = nameof(FormWords.Title), ResourceType = typeof(FormWords))]
    public string Title { get; set; } = "";

    [MaxLength(200, ErrorMessageResourceType = typeof(FormWords), ErrorMessageResourceName = nameof(FormWords.TooLong)), Display(Name = nameof(FormWords.Subtitle), ResourceType = typeof(FormWords))]
    public string Subtitle { get; set; } = "";

    [Required(ErrorMessageResourceType = typeof(FormWords), ErrorMessageResourceName = nameof(FormWords.Required)), MaxLength(200, ErrorMessageResourceType = typeof(FormWords), ErrorMessageResourceName = nameof(FormWords.TooLong)), Display(Name = nameof(FormWords.Author), ResourceType = typeof(FormWords))]
    public string Author { get; set; } = "";

    public BookStatus Status { get; set; } = BookStatus.Want;

    [Range(1, 5, ErrorMessageResourceType = typeof(FormWords), ErrorMessageResourceName = nameof(FormWords.OutOfRange)), Display(Name = nameof(FormWords.Rating), ResourceType = typeof(FormWords))]
    public int? Rating { get; set; }

    [Range(1000, 2100, ErrorMessageResourceType = typeof(FormWords), ErrorMessageResourceName = nameof(FormWords.OutOfRange)), Display(Name = nameof(FormWords.Year), ResourceType = typeof(FormWords))]
    public int? Year { get; set; }

    [MaxLength(32, ErrorMessageResourceType = typeof(FormWords), ErrorMessageResourceName = nameof(FormWords.TooLong)), Display(Name = nameof(FormWords.Isbn), ResourceType = typeof(FormWords))]
    public string Isbn { get; set; } = "";

    [Range(1, 20000, ErrorMessageResourceType = typeof(FormWords), ErrorMessageResourceName = nameof(FormWords.OutOfRange)), Display(Name = nameof(FormWords.Pages), ResourceType = typeof(FormWords))]
    public int? Pages { get; set; }

    [Range(0, 20000, ErrorMessageResourceType = typeof(FormWords), ErrorMessageResourceName = nameof(FormWords.OutOfRange)), Display(Name = nameof(FormWords.CurrentPage), ResourceType = typeof(FormWords))]
    public int? CurrentPage { get; set; }

    [MaxLength(4000, ErrorMessageResourceType = typeof(FormWords), ErrorMessageResourceName = nameof(FormWords.TooLong)), Display(Name = nameof(FormWords.Notes), ResourceType = typeof(FormWords))]
    public string Notes { get; set; } = "";

    public DateOnly? StartedOn { get; set; }

    public DateOnly? FinishedOn { get; set; }

    [MaxLength(120, ErrorMessageResourceType = typeof(FormWords), ErrorMessageResourceName = nameof(FormWords.TooLong)), Display(Name = nameof(FormWords.LoanedTo), ResourceType = typeof(FormWords))]
    public string LoanedTo { get; set; } = "";

    public DateOnly? LoanedOn { get; set; }

    public DateOnly? DueOn { get; set; }

    [MaxLength(200, ErrorMessageResourceType = typeof(FormWords), ErrorMessageResourceName = nameof(FormWords.TooLong)), Display(Name = nameof(FormWords.Publisher), ResourceType = typeof(FormWords))]
    public string Publisher { get; set; } = "";

    [MaxLength(40, ErrorMessageResourceType = typeof(FormWords), ErrorMessageResourceName = nameof(FormWords.TooLong)), Display(Name = nameof(FormWords.Language), ResourceType = typeof(FormWords))]
    public string Language { get; set; } = "";

    public string Format { get; set; } = "";

    [MaxLength(200, ErrorMessageResourceType = typeof(FormWords), ErrorMessageResourceName = nameof(FormWords.TooLong)), Display(Name = nameof(FormWords.Series), ResourceType = typeof(FormWords))]
    public string Series { get; set; } = "";

    [Range(1, 999, ErrorMessageResourceType = typeof(FormWords), ErrorMessageResourceName = nameof(FormWords.OutOfRange)), Display(Name = nameof(FormWords.SeriesNumber), ResourceType = typeof(FormWords))]
    public int? SeriesNumber { get; set; }

    [MaxLength(500, ErrorMessageResourceType = typeof(FormWords), ErrorMessageResourceName = nameof(FormWords.TooLong)), Display(Name = nameof(FormWords.CoverUrl), ResourceType = typeof(FormWords))]
    public string CoverUrl { get; set; } = "";

    [MaxLength(4000, ErrorMessageResourceType = typeof(FormWords), ErrorMessageResourceName = nameof(FormWords.TooLong)), Display(Name = nameof(FormWords.Review), ResourceType = typeof(FormWords))]
    public string Review { get; set; } = "";

    public bool Loved { get; set; }

    public bool Queued { get; set; }

    [MaxLength(80, ErrorMessageResourceType = typeof(FormWords), ErrorMessageResourceName = nameof(FormWords.TooLong)), Display(Name = nameof(FormWords.Location), ResourceType = typeof(FormWords))]
    public string Location { get; set; } = "";

    public DateOnly? AcquiredOn { get; set; }

    public string Arrival { get; set; } = "";

    public string Condition { get; set; } = "";

    [MaxLength(200, ErrorMessageResourceType = typeof(FormWords), ErrorMessageResourceName = nameof(FormWords.TooLong)), Display(Name = nameof(FormWords.OriginalTitle), ResourceType = typeof(FormWords))]
    public string OriginalTitle { get; set; } = "";

    [MaxLength(500, ErrorMessageResourceType = typeof(FormWords), ErrorMessageResourceName = nameof(FormWords.TooLong)), Display(Name = nameof(FormWords.Inscription), ResourceType = typeof(FormWords))]
    public string Inscription { get; set; } = "";

    [MaxLength(200, ErrorMessageResourceType = typeof(FormWords), ErrorMessageResourceName = nameof(FormWords.TooLong)), Display(Name = nameof(FormWords.Translator), ResourceType = typeof(FormWords))]
    public string Translator { get; set; } = "";

    [MaxLength(200, ErrorMessageResourceType = typeof(FormWords), ErrorMessageResourceName = nameof(FormWords.TooLong)), Display(Name = nameof(FormWords.Narrator), ResourceType = typeof(FormWords))]
    public string Narrator { get; set; } = "";

    [MaxLength(120, ErrorMessageResourceType = typeof(FormWords), ErrorMessageResourceName = nameof(FormWords.TooLong)), Display(Name = nameof(FormWords.RecommendedBy), ResourceType = typeof(FormWords))]
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
        OriginalTitle,
        Inscription,
        DueOn,
        Queued,
        Enum.TryParse<Acquisition>(Arrival, out var acquisition) ? acquisition : null,
        Enum.TryParse<CopyCondition>(Condition, out var condition) ? condition : null,
        Narrator);

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

        filled |= Assign(Subtitle, match.Subtitle, value => Subtitle = value, 200);
        filled |= Assign(Isbn, match.Isbn, value => Isbn = value, 32);
        if (string.IsNullOrWhiteSpace(Format) && match.Format is { } format)
        {
            Format = format.ToString();
            filled = true;
        }

        var incoming = BookRules.UsefulSubjects(match.Tags);
        if (incoming.Length > 0)
        {
            var tags = string.IsNullOrWhiteSpace(Tags)
                ? []
                : Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            var added = false;
            foreach (var tag in incoming)
            {
                if (tags.Count >= BookRules.MaxTags || tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                tags.Add(tag);
                added = true;
            }

            if (added)
            {
                Tags = string.Join(", ", tags);
                filled = true;
            }
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
        DueOn = null;
        Publisher = "";
        Language = "";
        Format = "";
        Series = "";
        SeriesNumber = null;
        CoverUrl = "";
        Review = "";
        Loved = false;
        Queued = false;
        Location = "";
        AcquiredOn = null;
        Arrival = "";
        Condition = "";
        Translator = "";
        Narrator = "";
        OriginalTitle = "";
        Inscription = "";
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
        DueOn = book.DueOn,
        Publisher = book.Publisher ?? "",
        Language = book.Language ?? "",
        Format = book.Format?.ToString() ?? "",
        Series = book.Series ?? "",
        SeriesNumber = book.SeriesNumber,
        CoverUrl = book.CoverUrl ?? "",
        Review = book.Review ?? "",
        Loved = book.Loved,
        Queued = book.Queued,
        Location = book.Location ?? "",
        AcquiredOn = book.AcquiredOn,
        Arrival = book.Acquisition?.ToString() ?? "",
        Condition = book.Condition?.ToString() ?? "",
        Translator = book.Translator ?? "",
        Narrator = book.Narrator ?? "",
        OriginalTitle = book.OriginalTitle ?? "",
        Inscription = book.Inscription ?? "",
        RecommendedBy = book.RecommendedBy ?? "",
        Tags = string.Join(", ", book.Tags.Select(tag => tag.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)),
    };
}

public sealed class SessionFormModel
{
    public DateOnly? Date { get; set; }

    [Range(0, 20000, ErrorMessageResourceType = typeof(FormWords), ErrorMessageResourceName = nameof(FormWords.OutOfRange)), Display(Name = nameof(FormWords.FromPage), ResourceType = typeof(FormWords))]
    public int? FromPage { get; set; }

    [Range(0, 20000, ErrorMessageResourceType = typeof(FormWords), ErrorMessageResourceName = nameof(FormWords.OutOfRange)), Display(Name = nameof(FormWords.ToPage), ResourceType = typeof(FormWords))]
    public int? ToPage { get; set; }

    [MaxLength(500, ErrorMessageResourceType = typeof(FormWords), ErrorMessageResourceName = nameof(FormWords.TooLong)), Display(Name = nameof(FormWords.Note), ResourceType = typeof(FormWords))]
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
    [Range(0, 1000, ErrorMessageResourceType = typeof(FormWords), ErrorMessageResourceName = nameof(FormWords.OutOfRange)), Display(Name = nameof(FormWords.YearlyGoal), ResourceType = typeof(FormWords))]
    public int YearlyGoal { get; set; }
}

public sealed class QuoteFormModel
{
    [Required(ErrorMessageResourceType = typeof(FormWords), ErrorMessageResourceName = nameof(FormWords.Required)), MaxLength(1000, ErrorMessageResourceType = typeof(FormWords), ErrorMessageResourceName = nameof(FormWords.TooLong)), Display(Name = nameof(FormWords.Text), ResourceType = typeof(FormWords))]
    public string Text { get; set; } = "";

    [Range(1, 20000, ErrorMessageResourceType = typeof(FormWords), ErrorMessageResourceName = nameof(FormWords.OutOfRange)), Display(Name = nameof(FormWords.Page), ResourceType = typeof(FormWords))]
    public int? Page { get; set; }

    public void Clear()
    {
        Text = "";
        Page = null;
    }
}
