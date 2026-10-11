using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class InsightsTests
{
    private static Book Finished(int id, string title, string author, DateOnly on, int pages, params string[] tags) => new()
    {
        Id = id,
        Title = title,
        Author = author,
        Status = BookStatus.Finished,
        FinishedOn = on,
        Pages = pages,
        Tags = [.. tags.Select(name => new Tag { Name = name })],
    };

    [Fact]
    public void Milestones_mark_the_first_and_tenth_book_the_pages_and_the_goal()
    {
        var today = new DateOnly(2026, 10, 11);
        var books = Enumerable.Range(1, 10).Select(number => Finished(number, $"Book {number}", "Ursula K. Le Guin", today.AddDays(-100 + number * 5), 1100)).ToList();

        var milestones = Insights.Milestones(books, yearlyGoal: 8, today);

        Assert.Contains(milestones, item => item.Text.Contains("first finished book") && item.BookId == 1);
        Assert.Contains(milestones, item => item.Text.StartsWith("Book 10 finished") && item.BookId == 10);
        Assert.Contains(milestones, item => item.Text.StartsWith("10,000 pages read") && item.BookId == 10);
        Assert.Contains(milestones, item => item.Text.Contains("goal of 8 reached") && item.BookId == 8);
        Assert.Contains(milestones, item => item.Text.Contains("longest book"));
        Assert.True(milestones.Select(item => item.Day).SequenceEqual(milestones.Select(item => item.Day).OrderDescending()));
    }

    [Fact]
    public void Favourites_rank_authors_kinds_and_series_by_books_finished()
    {
        var day = new DateOnly(2026, 1, 1);
        var books = new List<Book>
        {
            Finished(1, "A Wizard of Earthsea", "Ursula K. Le Guin", day, 200, "fantasy"),
            Finished(2, "The Tombs of Atuan", "Ursula K. Le Guin", day, 180, "fantasy"),
            Finished(3, "Dune", "Frank Herbert", day, 600, "science fiction"),
            new() { Id = 4, Title = "Tehanu", Author = "Ursula K. Le Guin", Status = BookStatus.Want, Series = "Earthsea" },
        };
        books[0].Series = books[1].Series = "Earthsea";

        var (authors, kinds, series) = Insights.Favourites(books);

        Assert.Equal(("Ursula K. Le Guin", 2), (authors[0].Name, authors[0].Count));
        Assert.Equal("fantasy", kinds[0].Name);
        Assert.Equal(("Earthsea", 2), (series[0].Name, series[0].Count));
        Assert.Contains("author=Ursula%20K.%20Le%20Guin", authors[0].Href);
    }
}
