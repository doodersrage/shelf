using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// An Audiobookshelf item that came across, so keeping in step knows it, whatever it is called on either side later.
public sealed class AbsLink
{
    public int Id { get; set; }
    public int ReaderId { get; set; }
    public required string Server { get; set; }
    public required string ItemId { get; set; }
    public int BookId { get; set; }

    // The reader's listening history for this book has come across, so it is never added twice.
    public bool HistoryBrought { get; set; }
}

// A library kept in step: every day, books new to it come across to the reader's shelf. Its API key is kept,
// sealed with the shelf's own keys, since a password's sign-in does not last.
public sealed class AbsSyncSetting
{
    public int Id { get; set; }
    public int ReaderId { get; set; }
    public required string Server { get; set; }
    public required string LibraryId { get; set; }
    public required string LibraryName { get; set; }
    public required string SealedKey { get; set; }
    public bool BringProgress { get; set; }
    public DateTimeOffset? CheckedAt { get; set; }
    public int? LastFound { get; set; }
    public string? Problem { get; set; }
    public bool CheckSoon { get; set; }
}

public sealed record AbsSyncState(string Server, string LibraryName, DateTimeOffset? CheckedAt, int? LastFound, string? Problem);

public sealed class AbsKeeper(IServiceScopeFactory scopes, IHttpClientFactory clients, IDataProtectionProvider protection, AbsImporter importer, IConfiguration configuration, ILogger<AbsKeeper> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromDays(1);

    private IDataProtector Seal => protection.CreateProtector("Shelf.AbsSync");

    public static string ServerKey(Uri server) => server.GetLeftPart(UriPartial.Path).TrimEnd('/');

    public static async Task<AbsSyncState?> StateAsync(ShelfDb db, CancellationToken cancellationToken = default) =>
        await db.AbsSyncs.AsNoTracking().Where(item => item.ReaderId == db.ReaderId)
            .Select(item => new AbsSyncState(item.Server, item.LibraryName, item.CheckedAt, item.LastFound, item.Problem))
            .FirstOrDefaultAsync(cancellationToken);

    // One library kept in step for each reader; choosing another replaces it.
    public async Task KeepAsync(ShelfDb db, Uri server, string apiKey, string libraryId, string libraryName, bool bringProgress, CancellationToken cancellationToken = default)
    {
        var setting = await db.AbsSyncs.FirstOrDefaultAsync(item => item.ReaderId == db.ReaderId, cancellationToken);
        if (setting is null)
        {
            setting = new AbsSyncSetting { ReaderId = db.ReaderId, Server = "", LibraryId = "", LibraryName = "", SealedKey = "" };
            db.AbsSyncs.Add(setting);
        }

        (setting.Server, setting.LibraryId, setting.LibraryName, setting.SealedKey, setting.BringProgress) = (ServerKey(server), libraryId, libraryName, Seal.Protect(apiKey), bringProgress);
        // The import just started brings what is there now; the first look comes a day later.
        (setting.CheckedAt, setting.Problem, setting.LastFound, setting.CheckSoon) = (DateTimeOffset.UtcNow, null, null, false);
        await db.SaveChangesAsync(cancellationToken);
    }

    public static async Task StopAsync(ShelfDb db, CancellationToken cancellationToken = default) =>
        await db.AbsSyncs.Where(item => item.ReaderId == db.ReaderId).ExecuteDeleteAsync(cancellationToken);

    public static async Task CheckSoonAsync(ShelfDb db, CancellationToken cancellationToken = default) =>
        await db.AbsSyncs.Where(item => item.ReaderId == db.ReaderId).ExecuteUpdateAsync(set => set.SetProperty(item => item.CheckSoon, true), cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("AbsSync:Enabled", true))
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(configuration.GetValue("AbsSync:LookSeconds", 30)));
        do
        {
            try
            {
                await LookAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Keeping in step with Audiobookshelf failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // Each library due a look: a day since the last, or asked for now.
    public async Task LookAsync(CancellationToken cancellationToken)
    {
        List<int> due;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var before = DateTimeOffset.UtcNow - Every;
            due = (await db.AbsSyncs.AsNoTracking().ToListAsync(cancellationToken))
                .Where(item => item.CheckSoon || item.CheckedAt is null || item.CheckedAt < before)
                .Select(item => item.Id)
                .ToList();
        }

        foreach (var id in due)
        {
            await CheckAsync(id, cancellationToken);
        }
    }

    // Lists the library and brings across what has come to it since: items never brought, and not already on the
    // shelf by title and author.
    public async Task<int> CheckAsync(int settingId, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var setting = await db.AbsSyncs.FirstOrDefaultAsync(item => item.Id == settingId, cancellationToken);
        if (setting is null)
        {
            return 0;
        }

        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(setting.ReaderId);
        (setting.CheckedAt, setting.CheckSoon) = (DateTimeOffset.UtcNow, false);
        // Not while an import of this reader's is still running; the next look picks up what it leaves.
        if (importer.Latest(setting.ReaderId) is { Finished: false })
        {
            await db.SaveChangesAsync(cancellationToken);
            return 0;
        }

        try
        {
            var http = clients.CreateClient(AbsClient.ClientFor(await ReaderRules.IsAdminAsync(db, setting.ReaderId, cancellationToken)));
            var connection = await AbsClient.ConnectAsync(http, new Uri(setting.Server + "/"), Seal.Unprotect(setting.SealedKey), null, null, cancellationToken);
            var items = await AbsClient.ItemsAsync(http, connection, setting.LibraryId, cancellationToken);
            var brought = (await db.AbsLinks.AsNoTracking().Where(link => link.ReaderId == setting.ReaderId && link.Server == setting.Server).Select(link => link.ItemId).ToListAsync(cancellationToken)).ToHashSet();
            var shelf = (await db.Books.AsNoTracking().Select(book => new { book.Title, book.Author }).ToListAsync(cancellationToken))
                .Select(book => Key(book.Title, book.Author)).ToHashSet();
            var fresh = items.Where(item => !brought.Contains(item.Id) && !shelf.Contains(Key(item.Title, item.Author))).ToList();
            if (fresh.Count > 0)
            {
                importer.Start(setting.ReaderId, connection, AbsOptions.Place(setting.BringProgress), fresh);
            }

            (setting.LastFound, setting.Problem) = (fresh.Count, null);
            await db.SaveChangesAsync(cancellationToken);
            return fresh.Count;
        }
        catch (Exception ex) when (ex is AbsProblem or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException or System.Security.Cryptography.CryptographicException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            setting.Problem = ex switch
            {
                AbsProblem => ex.Message,
                System.Security.Cryptography.CryptographicException => T("The kept API key can no longer be read. Connect again to keep in step."),
                _ => T("Audiobookshelf did not answer."),
            };
            await db.SaveChangesAsync(cancellationToken);
            return 0;
        }
    }

    public static string Key(string title, string author) => $"{title.Trim().ToUpperInvariant()}\u0001{author.Trim().ToUpperInvariant()}";
}
