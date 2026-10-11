using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public sealed record ChapterMark(string Title, double Start);

// Chapters set by hand for an audiobook: names and where each starts in the whole recording. They are kept where
// Audible's go (Book.AudioChapterNames), so the player places them across the tracks the same way.
public static class ChapterEditing
{
    public const int MostChapters = 1000;

    public static void Map(RouteGroupBuilder books)
    {
        books.MapPut("/{id:int}/audio/chapters", async Task<Results<NoContent, NotFound, ValidationProblem>> (int id, ChapterMark[] chapters, ShelfDb db, AudioStore audio, CancellationToken cancellationToken) =>
            await SaveAsync(db, audio, id, chapters, cancellationToken) switch
            {
                null => TypedResults.NoContent(),
                "" => TypedResults.NotFound(),
                var problem => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["chapters"] = [problem] }),
            });
        books.MapDelete("/{id:int}/audio/chapters", async Task<Results<NoContent, NotFound>> (int id, ShelfDb db, CancellationToken cancellationToken) =>
        {
            if (!await db.Books.AnyAsync(book => book.Id == id, cancellationToken))
            {
                return TypedResults.NotFound();
            }

            await Audible.ForgetChaptersAsync(db, id, cancellationToken);
            return TypedResults.NoContent();
        });
    }

    // Null when saved; an empty problem when there is no such book of the reader's.
    public static async Task<string?> SaveAsync(ShelfDb db, AudioStore audio, int id, IReadOnlyList<ChapterMark>? chapters, CancellationToken cancellationToken = default)
    {
        var book = await db.Books.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (book is null || string.IsNullOrWhiteSpace(book.AudioStoredName))
        {
            return "";
        }

        var lengths = audio.Lengths(book.AudioStoredName);
        if (lengths.Any(length => length is null))
        {
            return T("Shelf does not know how long every track is, so chapters cannot be placed in them.");
        }

        var total = lengths.Sum(length => length!.Value);
        var marks = (chapters ?? []).Select(chapter => new ChapterMark((chapter.Title ?? "").Trim(), Math.Round(chapter.Start, 2))).OrderBy(chapter => chapter.Start).ToList();
        if (marks.Count is < 2 or > MostChapters)
        {
            return T("Give at least two chapters, and no more than {0}.", MostChapters);
        }

        if (marks.Any(chapter => chapter.Title.Length is 0 or > 200))
        {
            return T("Give every chapter a name of up to 200 characters.");
        }

        if (marks.Any(chapter => double.IsNaN(chapter.Start) || chapter.Start < 0 || chapter.Start >= total))
        {
            return T("Every chapter must start within the recording, which is {0} long.", Clock(total));
        }

        if (marks.Zip(marks.Skip(1)).Any(pair => pair.Second.Start - pair.First.Start < 1))
        {
            return T("Two chapters start at the same moment.");
        }

        book.AudioChapterNames = JsonSerializer.Serialize(marks.Select(mark => new AudibleChapter(mark.Title, mark.Start)));
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    // "1:02:03", "62:03", or seconds, as a person types a time.
    public static double? ParseClock(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var seconds = 0.0;
        foreach (var part in text.Trim().Split(':'))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number < 0)
            {
                return null;
            }

            seconds = (seconds * 60) + number;
        }

        return seconds;
    }

    public static string Clock(double seconds)
    {
        var whole = (int)Math.Floor(seconds);
        return whole >= 3600 ? $"{whole / 3600}:{whole / 60 % 60:00}:{whole % 60:00}" : $"{whole / 60}:{whole % 60:00}";
    }
}
