using Microsoft.AspNetCore.Http.HttpResults;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public enum ImportLineState
{
    Waiting,
    Working,
    Done,
    Skipped,
    Failed,
}

// One book of an import, however it came: its title, how it went, and where it landed.
public sealed record ImportLine(string Title, ImportLineState State, string? Note, int? BookId, long Bytes);

// An import of many books running in the background (from Audiobookshelf, from Calibre), as the pages and the
// notice on every page show it.
public interface IImportJob
{
    string Source { get; }
    string Page { get; }
    int ReaderId { get; }
    DateTimeOffset Started { get; }
    bool Finished { get; }
    bool Stopping { get; }

    // Asks it to stop after the book it is on; the rest stay where they are.
    void Stop();

    // Every book, in the order it is brought.
    IReadOnlyList<ImportLine> Lines();
}

public sealed record ImportSummary(
    string Source,
    string Page,
    int Total,
    int Done,
    int Skipped,
    int Failed,
    int Left,
    string? Current,
    bool Finished,
    bool Stopping,
    int? MinutesLeft)
{
    // The line the notice shows, in the reader's language.
    public string Text => ImportJobs.Describe(this);
}

public static class ImportJobs
{
    // How far it has come, and how long the rest may take at the pace so far.
    public static ImportSummary Summarize(IImportJob job, DateTimeOffset now)
    {
        var lines = job.Lines();
        var done = lines.Count(line => line.State == ImportLineState.Done);
        var skipped = lines.Count(line => line.State == ImportLineState.Skipped);
        var failed = lines.Count(line => line.State == ImportLineState.Failed);
        var left = lines.Count(line => line.State is ImportLineState.Waiting or ImportLineState.Working);
        var handled = lines.Count - left;
        var elapsed = now - job.Started;
        int? minutes = !job.Finished && handled >= 3 && left > 0 && elapsed > TimeSpan.FromSeconds(20)
            ? (int)Math.Ceiling(elapsed.TotalMinutes / handled * left)
            : null;
        var current = lines.FirstOrDefault(line => line.State == ImportLineState.Working)?.Title;
        return new ImportSummary(job.Source, job.Page, lines.Count, done, skipped, failed, left, current, job.Finished, job.Stopping, minutes);
    }

    // The reader's import running now, from whichever importer, for the notice on every page.
    public static IImportJob? Running(IServiceProvider services, int readerId) =>
        new IImportJob?[] { services.GetService<AbsImporter>()?.Latest(readerId), services.GetService<CalibreImporter>()?.Latest(readerId) }
            .OfType<IImportJob>()
            .Where(job => !job.Finished)
            .OrderByDescending(job => job.Started)
            .FirstOrDefault();

    public static string Describe(ImportSummary summary)
    {
        var text = T("{0} of {1} handled", (summary.Total - summary.Left).ToString("N0"), summary.Total.ToString("N0"));
        if (summary.MinutesLeft is int minutes)
        {
            text += " · " + (minutes >= 90 ? T("about {0} h left", (int)Math.Round(minutes / 60.0)) : T("about {0} min left", Math.Max(1, minutes)));
        }

        return text;
    }

    // What the notice polls while an import runs, so it moves without the page being reloaded.
    public static void Map(RouteGroupBuilder books) =>
        books.MapGet("/imports/running", Results<Ok<ImportSummary>, NoContent> (HttpContext http, ShelfDb db) =>
            Running(http.RequestServices, db.ReaderId) is { } job ? TypedResults.Ok(Summarize(job, DateTimeOffset.UtcNow)) : TypedResults.NoContent());
}
