using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

public sealed record AutomaticBackup(string Name, long Bytes, DateTimeOffset TakenAt);

// A snapshot of the database every night, the last few kept beside it. E-books and audiobooks never change
// once saved, so they are left out unless Backup:IncludeFiles asks for them; copying their folders is enough.
public sealed class BackupSchedule(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    EbookStore ebooks,
    AudioStore audio,
    CoverStore covers,
    OffsiteBackup offsite,
    ILogger<BackupSchedule> logger) : BackgroundService
{
    public const string Prefix = "shelf-backup-";

    public bool Enabled => configuration.GetValue("Backup:Enabled", true);

    public int Keep => Math.Max(1, configuration.GetValue("Backup:Keep", 7));

    public int Hour => Math.Clamp(configuration.GetValue("Backup:Hour", 3), 0, 23);

    public bool IncludeFiles => configuration.GetValue("Backup:IncludeFiles", false);

    public string Folder { get; } = configuration["Backup:Folder"] ?? Path.Combine(DataFolder(configuration), "backups");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        do
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                if (now.Hour >= Hour && !List().Any(backup => backup.TakenAt.UtcDateTime.Date == now.UtcDateTime.Date))
                {
                    await TakeAsync(stoppingToken);
                }

                Heartbeats.Beat(nameof(BackupSchedule));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Heartbeats.Beat(nameof(BackupSchedule), ex.Message);
                logger.LogWarning(ex, "The nightly backup failed; it will try again shortly.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public bool BeforeMigrating => configuration.GetValue("Backup:BeforeMigrating", true);

    // Before a new version changes the database, a copy of it as it was, among the nightly backups: the database and
    // covers, as e-books and audiobooks are never changed by it. Only for a shelf with data and changes waiting, never
    // a new one. Null when done or not needed; otherwise why it could not be, and the database is left as it was.
    public async Task<string?> BeforeMigratingAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        if (!BeforeMigrating)
        {
            return null;
        }

        var waiting = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Count();
        if (waiting == 0 || !(await db.Database.GetAppliedMigrationsAsync(cancellationToken)).Any())
        {
            return null;
        }

        var name = $"{Prefix}{DateTimeOffset.UtcNow:yyyy-MM-dd-HHmmssfff}-before-upgrade.zip";
        var path = Path.Combine(Folder, name);
        var partial = path + ".partial";
        try
        {
            Directory.CreateDirectory(Folder);
            var whole = await Backup.WriteSnapshotAsync(db, ebooks, audio, covers, partial, cancellationToken, includeFiles: false, pgDump: configuration["Database:PgDump"]);
            File.Move(partial, path);
            if (whole)
            {
                logger.LogInformation("Took the backup {Name} before bringing the database up to this version ({Count} changes).", name, waiting);
            }
            else
            {
                // PostgreSQL without pg_dump: as with the nightly backups, there is nothing to copy the database with.
                logger.LogWarning("Bringing the database up to this version without a copy of it first: {Problem}", Backup.NoDatabaseProblem());
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                File.Delete(partial);
            }
            catch (IOException)
            {
            }

            return $"Shelf left its database as it was: it brings the database up to a new version only after backing it up, and could not back it up to {Folder}: {ex.Message} Make room there or let Shelf write to it, or set Backup__BeforeMigrating=false to go on without the backup.";
        }
    }

    public async Task<AutomaticBackup> TakeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Folder);
        var name = $"{Prefix}{DateTimeOffset.UtcNow:yyyy-MM-dd-HHmmssfff}.zip";
        var path = Path.Combine(Folder, name);
        var partial = path + ".partial";
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            await Backup.WriteSnapshotAsync(db, ebooks, audio, covers, partial, cancellationToken, IncludeFiles, configuration["Database:PgDump"]);
        }
        catch
        {
            // A half-written backup is never kept to fill the disk; failing to clear it never hides why it failed.
            try
            {
                File.Delete(partial);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }

        // Only a finished zip carries the real name, so a half-written one is never mistaken for a backup.
        File.Move(partial, path);
        logger.LogInformation("Took the nightly backup {Name}.", name);

        // Proved before anything is let go or sent anywhere: a backup that cannot be restored is worse than knowing
        // there is none, and must never push out one that can.
        var verdict = await BackupCheck.DrillAndKeepAsync(path, cancellationToken);
        foreach (var old in ToLetGo(List(), Keep, backup => BackupCheck.VerdictAt(Path.Combine(Folder, backup)) is { Ok: true }))
        {
            File.Delete(Path.Combine(Folder, old));
            File.Delete(Path.Combine(Folder, old + ".check.json"));
        }

        if (verdict.Ok)
        {
            await offsite.SendAsync(path, Keep, cancellationToken);
        }
        else
        {
            logger.LogWarning("The backup {Name} did not pass its restore drill, so it was not copied elsewhere: {Problem}", name, verdict.Problem);
        }

        return List().First(backup => backup.Name == name);
    }

    // The backups past the newest Keep, newest first given, but never the newest that passed its drill: however many
    // fail after it, the last backup that restores stays until a newer one does.
    public static IEnumerable<string> ToLetGo(IReadOnlyList<AutomaticBackup> newestFirst, int keep, Func<string, bool> proved)
    {
        var lastGood = newestFirst.FirstOrDefault(backup => proved(backup.Name))?.Name;
        return newestFirst.Skip(keep).Select(backup => backup.Name).Where(name => name != lastGood).ToList();
    }

    public IReadOnlyList<AutomaticBackup> List()
    {
        if (!Directory.Exists(Folder))
        {
            return [];
        }

        return Directory.EnumerateFiles(Folder, Prefix + "*.zip")
            .Select(path => new FileInfo(path))
            .Select(file => new AutomaticBackup(file.Name, file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)))
            .OrderByDescending(backup => backup.TakenAt)
            .ToList();
    }

    public string? PathOf(string name)
    {
        var file = Path.GetFileName(name);
        var path = Path.Combine(Folder, file);
        return file == name && file.StartsWith(Prefix, StringComparison.Ordinal) && file.EndsWith(".zip", StringComparison.Ordinal) && File.Exists(path)
            ? path
            : null;
    }

    public static string DataFolder(IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("sqlite") ?? configuration.GetConnectionString("Shelf") ?? "Data Source=shelf.db";
        var source = new SqliteConnectionStringBuilder(connection).DataSource;
        return Path.GetDirectoryName(Path.GetFullPath(source)) ?? AppContext.BaseDirectory;
    }
}

// Ready only when the database answers.
public sealed class DatabaseHealthCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        return await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("The database does not answer.");
    }
}
