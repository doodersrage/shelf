using Microsoft.AspNetCore.Http.HttpResults;
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
        admin.MapGet("/backups", (BackupSchedule schedule) => TypedResults.Ok(schedule.List()));
        admin.MapPost("/backups", async (BackupSchedule schedule, ShelfDb db, CancellationToken cancellationToken) =>
        {
            var taken = await schedule.TakeAsync(cancellationToken);
            await Audit.NoteAsync(db, Localization.Words.Say("Took a backup"), detail: taken.Name, cancellationToken: cancellationToken);
            return TypedResults.Ok(taken);
        });
        admin.MapGet("/audit", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await Audit.RecentAsync(db, take: 500, cancellationToken: cancellationToken)));
        admin.MapGet("/backups/{name}", (string name, BackupSchedule schedule) =>
            schedule.PathOf(name) is { } path ? Results.File(path, "application/zip", name) : Results.NotFound());
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

    private static async Task<TempFileResult> Snapshot(ShelfDb db, EbookStore ebooks, AudioStore audio, CoverStore covers, CancellationToken cancellationToken)
    {
        var path = TempFileResult.NewPath(".zip");
        await Backup.WriteSnapshotAsync(db, ebooks, audio, covers, path, cancellationToken);
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
