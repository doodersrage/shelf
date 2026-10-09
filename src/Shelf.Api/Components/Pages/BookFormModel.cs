using System.ComponentModel.DataAnnotations;
using Shelf.Api.Books;

namespace Shelf.Api.Components.Pages;

public sealed class BookFormModel
{
    [Required, MaxLength(200)]
    public string Title { get; set; } = "";

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

    public string Tags { get; set; } = "";

    public BookWrite ToWrite() => new(
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
            : Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

    public void Clear()
    {
        Title = "";
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
        Tags = "";
    }

    public static BookFormModel From(Book book) => new()
    {
        Title = book.Title,
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
        Tags = string.Join(", ", book.Tags.Select(tag => tag.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)),
    };
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
