using System.ComponentModel.DataAnnotations;
using Shelf.Api.Books;

namespace Shelf.Api.Components.Pages;

public sealed class NewBookModel
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
}
