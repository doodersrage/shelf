using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Shelf.Api.Books;
using Shelf.Api.Data;

namespace Shelf.Api.Readers;

public static class AdminEndpoints
{
    public static void MapAdmin(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/admin").WithTags("Admin").RequireAuthorization().AddEndpointFilter(AdminsOnly);
        admin.MapGet("/readers", Readers);
        admin.MapPost("/readers/{id:int}/password", ResetPassword);
        admin.MapPut("/readers/{id:int}/admin", SetAdmin);
        admin.MapDelete("/readers/{id:int}", Remove);
        admin.MapGet("/snapshot", Snapshot);
        admin.MapGet("/backups", (BackupCheck check) => TypedResults.Ok(check.List()));
        admin.MapPost("/backups/{name}/drill", async Task<IResult> (string name, BackupCheck check, CancellationToken cancellationToken) =>
            await check.DrillAsync(name, cancellationToken) is { } verdict ? TypedResults.Ok(verdict) : TypedResults.NotFound());
        admin.MapPost("/backups", async (BackupSchedule schedule, ShelfDb db, CancellationToken cancellationToken) =>
        {
            var taken = await schedule.TakeAsync(cancellationToken);
            await Audit.NoteAsync(db, Localization.Words.Say("Took a backup"), detail: taken.Name, cancellationToken: cancellationToken);
            return TypedResults.Ok(taken);
        });
        admin.MapGet("/audit", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await Audit.RecentAsync(db, take: 500, cancellationToken: cancellationToken)));
        admin.MapGet("/backups/{name}", (string name, BackupSchedule schedule) =>
            schedule.PathOf(name) is { } path ? Results.File(path, "application/zip", name) : Results.NotFound());

        // Readers, as an admin manages them.
        admin.MapGet("/accounts", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await Management.ReadersAsync(db, cancellationToken)));
        admin.MapPut("/readers/{id:int}/suspended", async (int id, SuspendRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            Answer(await Management.SuspendAsync(db, id, request.Suspended, cancellationToken)));
        admin.MapPost("/readers/{id:int}/signout", async Task<IResult> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await Management.SignOutEverywhereAsync(db, id, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound());
        admin.MapPut("/readers/{id:int}/name", async (int id, RenameRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            Answer(await Management.RenameAsync(db, id, request.Name, cancellationToken)));
        admin.MapPut("/readers/{id:int}/email", async (int id, KindleSettingsRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            Answer(await db.Readers.AsNoTracking().Where(reader => reader.Id == id).Select(reader => (bool?)reader.EmailNotices).FirstOrDefaultAsync(cancellationToken) is bool notices
                ? await ReaderRules.SetEmailAsync(db, id, request.Email, notices, cancellationToken)
                : AccountProblem.NoSuchReader));

        // Invitations, and who may sign up.
        admin.MapGet("/invitations", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await Management.InvitationsAsync(db, cancellationToken)));
        admin.MapPost("/invitations", async (InvitationRequest request, HttpContext http, ShelfDb db, IEmailSender email, IConfiguration configuration, CancellationToken cancellationToken) =>
            TypedResults.Ok(await Management.InviteAsync(db, email, request.Emails, EmailRules.PublicAddress(configuration, http.Request), cancellationToken)));
        admin.MapPost("/invitations/{id:int}/resend", async Task<IResult> (int id, HttpContext http, ShelfDb db, IEmailSender email, IConfiguration configuration, CancellationToken cancellationToken) =>
            await Management.ResendAsync(db, email, id, EmailRules.PublicAddress(configuration, http.Request), cancellationToken) is { } made ? TypedResults.Ok(made) : TypedResults.NotFound());
        admin.MapDelete("/invitations/{id:int}", async Task<IResult> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await Management.RevokeAsync(db, id, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound());
        admin.MapGet("/signup", async (ShelfDb db, IConfiguration configuration, CancellationToken cancellationToken) =>
            TypedResults.Ok(new SignUpModeRequest(await Management.SignUpModeAsync(db, configuration, cancellationToken))));
        admin.MapPut("/signup", async (SignUpModeRequest request, ShelfDb db, CancellationToken cancellationToken) =>
        {
            await Management.SetSignUpModeAsync(db, request.Mode, cancellationToken);
            return TypedResults.NoContent();
        });

        // The shelf as a whole: a note for everyone, the jobs, and a test email.
        admin.MapPut("/announcement", async (AnnouncementRequest request, HttpContext http, ShelfDb db, IMemoryCache cache, IEmailSender email, IConfiguration configuration, CancellationToken cancellationToken) =>
            TypedResults.Ok(new { emailed = await Management.AnnounceAsync(db, cache, email, request.Text, request.Email, EmailRules.PublicAddress(configuration, http.Request), cancellationToken) }));
        admin.MapGet("/jobs", () => TypedResults.Ok(Management.Jobs.Select(job => new { job.Key, job.Label, LastRun = Heartbeats.All.FirstOrDefault(beat => beat.Job == job.Beat) })));
        admin.MapPost("/jobs/{key}", async Task<IResult> (string key, IServiceProvider services, ShelfDb db, CancellationToken cancellationToken) =>
        {
            if (!await Management.RunAsync(services, key, cancellationToken))
            {
                return TypedResults.NotFound();
            }

            await Audit.NoteAsync(db, Localization.Words.Say("Ran a job by hand"), detail: key, cancellationToken: cancellationToken);
            return TypedResults.NoContent();
        });
    }

    private static async ValueTask<object?> AdminsOnly(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var db = context.HttpContext.RequestServices.GetRequiredService<ShelfDb>();
        return await ReaderRules.IsAdminAsync(db, db.ReaderId, context.HttpContext.RequestAborted)
            ? await next(context)
            : TypedResults.Forbid();
    }

    private static async Task<Ok<ReaderSummary[]>> Readers(ShelfDb db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await ReaderRules.SummariesAsync(db, cancellationToken));

    private static async Task<Results<Ok<TemporaryPassword>, NotFound>> ResetPassword(int id, ShelfDb db, CancellationToken cancellationToken) =>
        await ReaderRules.ResetPasswordAsync(db, id, cancellationToken) is { } password
            ? TypedResults.Ok(new TemporaryPassword(password))
            : TypedResults.NotFound();

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> SetAdmin(
        int id,
        AdminChange change,
        ShelfDb db,
        CancellationToken cancellationToken) =>
        Answer(await ReaderRules.SetAdminAsync(db, id, change.IsAdmin, cancellationToken));

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> Remove(
        int id,
        ShelfDb db,
        EbookStore ebooks,
        AudioStore audio,
        CoverStore covers,
        CancellationToken cancellationToken) =>
        Answer(await ReaderRules.RemoveAsync(db, ebooks, audio, covers, id, cancellationToken));

    private static async Task<Results<TempFileResult, ProblemHttpResult>> Snapshot(ShelfDb db, EbookStore ebooks, AudioStore audio, CoverStore covers, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var path = TempFileResult.NewPath(".zip");
        if (!await Backup.WriteSnapshotAsync(db, ebooks, audio, covers, path, cancellationToken, pgDump: configuration["Database:PgDump"]))
        {
            // A snapshot without the database would be mistaken for one that can bring the server back.
            File.Delete(path);
            return TypedResults.Problem(Backup.NoDatabaseProblem(), statusCode: StatusCodes.Status503ServiceUnavailable, title: Localization.Words.T("No snapshot without the database"));
        }

        await Audit.NoteAsync(db, Localization.Words.Say("Downloaded a snapshot of the whole server"), cancellationToken: cancellationToken);
        return new TempFileResult(path, "application/zip", $"shelf-snapshot-{DateTime.UtcNow:yyyy-MM-dd}.zip");
    }

    public static Results<NoContent, NotFound, ValidationProblem> Answer(AccountProblem? problem) => problem switch
    {
        null => TypedResults.NoContent(),
        AccountProblem.NoSuchReader => TypedResults.NotFound(),
        { } other => TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            ["Reader"] = [ReaderRules.Describe(other)],
        }),
    };
}
