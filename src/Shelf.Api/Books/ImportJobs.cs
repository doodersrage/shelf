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

// Work on many books running in the background (an import from Audiobookshelf or Calibre, filling in details,
// keeping in step with another server), as its page, the Activity page, and the notice on every page show it.
public interface IShelfJob
{
    Guid Id { get; }

    // "Bringing books from Audiobookshelf" while it runs, "Brought across from Audiobookshelf" once done.
    string Title(bool finished);

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
    Guid Id,
    string Title,
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
    public static ImportSummary Summarize(IShelfJob job, DateTimeOffset now)
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
        return new ImportSummary(job.Id, job.Title(job.Finished), job.Page, lines.Count, done, skipped, failed, left, current, job.Finished, job.Stopping, minutes);
    }

    // The reader's job running now, for the notice on every page.
    public static IShelfJob? Running(IServiceProvider services, int readerId) =>
        services.GetRequiredService<ShelfJobs>().Of(readerId).FirstOrDefault(job => !job.Finished);

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

// Every job of the shelf's, from whichever service runs it, kept while the server runs: the running ones and the
// last few finished, for the Activity page and the notice.
public sealed class ShelfJobs
{
    private const int KeptPerReader = 20;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, IShelfJob> _jobs = new();

    public void Add(IShelfJob job)
    {
        _jobs[job.Id] = job;
        foreach (var old in Of(job.ReaderId).Where(item => item.Finished).Skip(KeptPerReader))
        {
            _jobs.TryRemove(old.Id, out _);
        }
    }

    // Newest first.
    public IReadOnlyList<IShelfJob> Of(int readerId) =>
        [.. _jobs.Values.Where(job => job.ReaderId == readerId).OrderByDescending(job => job.Started)];

    // Every reader's, newest first: for the admin's health page, which says only how many and how they went.
    public IReadOnlyList<IShelfJob> All => [.. _jobs.Values.OrderByDescending(job => job.Started)];

    public IShelfJob? Find(int readerId, Guid id) => _jobs.TryGetValue(id, out var job) && job.ReaderId == readerId ? job : null;
}
