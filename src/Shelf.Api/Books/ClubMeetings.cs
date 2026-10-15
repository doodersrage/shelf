using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// A club meeting about one of its books: when, how far everyone should have read by then, what to talk about, and
// afterwards what was said. The reader who started the club arranges meetings; any member sends questions. The time
// is kept as the moment it is, with the time zone it was arranged in, so a notification can say it as the arranger
// meant it and each member's page in their own time.
public sealed class ClubMeeting
{
    public const int MaxThroughLength = 200;
    public const int MaxAgendaLength = 2000;
    public const int MaxSummaryLength = 4000;
    public const int MaxTimeZoneLength = 64;

    public int Id { get; set; }
    public int ClubId { get; set; }
    public Club? Club { get; set; }
    public int ClubBookId { get; set; }
    public ClubBook? Book { get; set; }
    public DateTimeOffset At { get; set; }

    // The IANA name of the zone it was arranged in, such as Europe/Paris; null for none known, taken as UTC.
    public string? TimeZone { get; set; }
    public string? Through { get; set; }
    public string? Agenda { get; set; }
    public string? Summary { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    // Whether members have been reminded the day before, and the hour before.
    public bool DayReminded { get; set; }
    public bool HourReminded { get; set; }
    public List<ClubQuestion> Questions { get; set; } = [];
}

// A question a member sends ahead of a meeting. Until the meeting begins only they and whoever runs it see it, so
// nobody's question steers anyone else's.
public sealed class ClubQuestion
{
    public const int MaxLength = 1000;

    public int Id { get; set; }
    public int MeetingId { get; set; }
    public ClubMeeting? Meeting { get; set; }
    public int ReaderId { get; set; }
    public required string Text { get; set; }
    public DateTimeOffset AskedAt { get; set; }
}

public sealed record ClubMeetingRequest(
    int ClubBookId,
    DateTimeOffset At,
    [MaxLength(ClubMeeting.MaxThroughLength)] string? Through = null,
    [MaxLength(ClubMeeting.MaxAgendaLength)] string? Agenda = null,
    [MaxLength(ClubMeeting.MaxTimeZoneLength)] string? TimeZone = null);

public sealed record ClubSummaryRequest([MaxLength(ClubMeeting.MaxSummaryLength)] string? Summary);

public sealed record ClubQuestionRequest([Required, MaxLength(ClubQuestion.MaxLength)] string Text);

public sealed record ClubQuestionResponse(int Id, string Reader, string Text, bool Mine);

// A meeting as a member sees it: the questions they may see so far, and how many are waiting to be seen.
public sealed record ClubMeetingResponse(
    int Id,
    int ClubBookId,
    string BookTitle,
    DateTimeOffset At,
    string? Through,
    string? Agenda,
    string? Summary,
    bool Begun,
    ClubQuestionResponse[] Questions,
    int Waiting,
    string? TimeZone = null);

public static class ClubMeetings
{
    public static void Map(RouteGroupBuilder books)
    {
        var meetings = books.MapGroup("/clubs/{id:int}/meetings");
        meetings.MapGet("/", async Task<IResult> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await ListAsync(db, id, cancellationToken) is { } found ? TypedResults.Ok(found) : TypedResults.NotFound());
        meetings.MapPost("/", async Task<IResult> (int id, ClubMeetingRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            await ArrangeAsync(db, id, request, cancellationToken) switch
            {
                (int made, ClubProblem.None) => TypedResults.Created($"/books/clubs/{id}/meetings/{made}", new { id = made }),
                (_, var problem) => Answer(problem),
            });
        meetings.MapPut("/{meetingId:int}/summary", async (int id, int meetingId, ClubSummaryRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            Answer(await SummarizeAsync(db, id, meetingId, request.Summary, cancellationToken)));
        meetings.MapDelete("/{meetingId:int}", async (int id, int meetingId, ShelfDb db, CancellationToken cancellationToken) =>
            Answer(await CancelAsync(db, id, meetingId, cancellationToken)));
        meetings.MapPost("/{meetingId:int}/questions", async (int id, int meetingId, ClubQuestionRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            Answer(await AskAsync(db, id, meetingId, request.Text, cancellationToken)));
    }

    private static IResult Answer(ClubProblem problem) => problem switch
    {
        ClubProblem.None => TypedResults.NoContent(),
        ClubProblem.NotFound => TypedResults.NotFound(),
        ClubProblem.NotOwner => TypedResults.Forbid(),
        _ => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Meeting"] = [T("That cannot be done in this club.")] }),
    };

    private static async Task<(bool Member, bool Owner)> StandingAsync(ShelfDb db, int clubId, CancellationToken cancellationToken)
    {
        var me = db.ReaderId;
        var club = await db.Clubs.AsNoTracking().Where(item => item.Id == clubId && me != 0 && item.Members.Any(member => member.ReaderId == me))
            .Select(item => new { item.OwnerId }).FirstOrDefaultAsync(cancellationToken);
        return (club is not null, club?.OwnerId == me);
    }

    // The club's meetings, the next first and then those gone by, latest first. Null for a club the reader isn't in.
    public static async Task<ClubMeetingResponse[]?> ListAsync(ShelfDb db, int clubId, CancellationToken cancellationToken = default)
    {
        var (member, owner) = await StandingAsync(db, clubId, cancellationToken);
        if (!member)
        {
            return null;
        }

        var me = db.ReaderId;
        var now = DateTimeOffset.UtcNow;
        var meetings = await db.ClubMeetings.AsNoTracking().Where(item => item.ClubId == clubId)
            .Include(item => item.Book).Include(item => item.Questions).AsSplitQuery()
            .ToListAsync(cancellationToken);
        var askers = meetings.SelectMany(item => item.Questions).Select(question => question.ReaderId).Distinct().ToList();
        var names = await db.Readers.AsNoTracking().Where(reader => askers.Contains(reader.Id)).ToDictionaryAsync(reader => reader.Id, reader => reader.Name, cancellationToken);
        var coming = meetings.Where(item => item.At > now).OrderBy(item => item.At);
        var past = meetings.Where(item => item.At <= now).OrderByDescending(item => item.At);
        return coming.Concat(past)
            .Select(item =>
            {
                var begun = item.At <= now;
                var seen = item.Questions.Where(question => begun || owner || question.ReaderId == me).OrderBy(question => question.AskedAt).ThenBy(question => question.Id).ToList();
                return new ClubMeetingResponse(
                    item.Id, item.ClubBookId, item.Book?.Title ?? "", item.At, item.Through, item.Agenda, item.Summary, begun,
                    seen.Select(question => new ClubQuestionResponse(question.Id, names.GetValueOrDefault(question.ReaderId, "?"), question.Text, question.ReaderId == me)).ToArray(),
                    item.Questions.Count - seen.Count,
                    item.TimeZone);
            })
            .ToArray();
    }

    public static async Task<(int? Id, ClubProblem Problem)> ArrangeAsync(ShelfDb db, int clubId, ClubMeetingRequest request, CancellationToken cancellationToken = default)
    {
        var (member, owner) = await StandingAsync(db, clubId, cancellationToken);
        if (!member)
        {
            return (null, ClubProblem.NotFound);
        }

        if (!owner)
        {
            return (null, ClubProblem.NotOwner);
        }

        var through = Trimmed(request.Through);
        var agenda = Trimmed(request.Agenda);
        if (!await db.ClubBooks.AnyAsync(book => book.Id == request.ClubBookId && book.ClubId == clubId, cancellationToken)
            || through?.Length > ClubMeeting.MaxThroughLength || agenda?.Length > ClubMeeting.MaxAgendaLength)
        {
            return (null, ClubProblem.Invalid);
        }

        var meeting = new ClubMeeting
        {
            ClubId = clubId,
            ClubBookId = request.ClubBookId,
            At = request.At.ToUniversalTime(),
            TimeZone = Zone(request.TimeZone) is { } zone ? zone.Id : null,
            Through = through,
            Agenda = agenda,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.ClubMeetings.Add(meeting);
        await db.SaveChangesAsync(cancellationToken);
        return (meeting.Id, ClubProblem.None);
    }

    public static async Task<ClubProblem> SummarizeAsync(ShelfDb db, int clubId, int meetingId, string? summary, CancellationToken cancellationToken = default)
    {
        var (member, owner) = await StandingAsync(db, clubId, cancellationToken);
        var meeting = member ? await db.ClubMeetings.FirstOrDefaultAsync(item => item.Id == meetingId && item.ClubId == clubId, cancellationToken) : null;
        if (meeting is null)
        {
            return ClubProblem.NotFound;
        }

        if (!owner)
        {
            return ClubProblem.NotOwner;
        }

        summary = Trimmed(summary);
        if (summary?.Length > ClubMeeting.MaxSummaryLength)
        {
            return ClubProblem.Invalid;
        }

        meeting.Summary = summary;
        await db.SaveChangesAsync(cancellationToken);
        return ClubProblem.None;
    }

    public static async Task<ClubProblem> CancelAsync(ShelfDb db, int clubId, int meetingId, CancellationToken cancellationToken = default)
    {
        var (member, owner) = await StandingAsync(db, clubId, cancellationToken);
        if (!member)
        {
            return ClubProblem.NotFound;
        }

        if (!owner)
        {
            return ClubProblem.NotOwner;
        }

        // Taken out as a tracked change, so the members hear that it is called off.
        var meeting = await db.ClubMeetings.FirstOrDefaultAsync(item => item.Id == meetingId && item.ClubId == clubId, cancellationToken);
        if (meeting is null)
        {
            return ClubProblem.NotFound;
        }

        db.ClubMeetings.Remove(meeting);
        await db.SaveChangesAsync(cancellationToken);
        return ClubProblem.None;
    }

    public static async Task<ClubProblem> AskAsync(ShelfDb db, int clubId, int meetingId, string? text, CancellationToken cancellationToken = default)
    {
        var (member, _) = await StandingAsync(db, clubId, cancellationToken);
        if (!member || !await db.ClubMeetings.AnyAsync(item => item.Id == meetingId && item.ClubId == clubId, cancellationToken))
        {
            return ClubProblem.NotFound;
        }

        text = Trimmed(text);
        if (text is null || text.Length > ClubQuestion.MaxLength)
        {
            return ClubProblem.Invalid;
        }

        db.ClubQuestions.Add(new ClubQuestion { MeetingId = meetingId, ReaderId = db.ReaderId, Text = text, AskedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(cancellationToken);
        return ClubProblem.None;
    }

    // A zone by its IANA name; null for none, or one the server doesn't know.
    public static TimeZoneInfo? Zone(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > ClubMeeting.MaxTimeZoneLength)
        {
            return null;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(name.Trim());
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    // A time typed on a page, in the zone of the browser it was typed in, as the moment it is.
    public static DateTimeOffset FromLocal(DateTime local, string? zoneName)
    {
        var zone = Zone(zoneName) ?? TimeZoneInfo.Utc;
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        // A time skipped when the clocks go forward is taken an hour later, as a clock on the wall would.
        if (zone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }

        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified)).ToUniversalTime();
    }

    // A meeting's time in a zone, with the zone named when it isn't the reader's own: for a page, a notification, or
    // a chat message.
    public static string Say(DateTimeOffset at, string? zoneName, bool nameZone = true)
    {
        var zone = Zone(zoneName) ?? TimeZoneInfo.Utc;
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var text = local.Medium() + " " + local.ToString("t", System.Globalization.CultureInfo.CurrentCulture);
        return nameZone ? $"{text} ({zone.Id})" : text;
    }

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
