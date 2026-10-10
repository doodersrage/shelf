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
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The nightly backup failed; it will try again shortly.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<AutomaticBackup> TakeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Folder);
        var name = $"{Prefix}{DateTimeOffset.UtcNow:yyyy-MM-dd-HHmmssfff}.zip";
        var path = Path.Combine(Folder, name);
        var partial = path + ".partial";
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            await Backup.WriteSnapshotAsync(db, ebooks, audio, partial, cancellationToken, IncludeFiles);
        }

        // Only a finished zip carries the real name, so a half-written one is never mistaken for a backup.
        File.Move(partial, path);
        foreach (var old in List().Skip(Keep))
        {
            File.Delete(Path.Combine(Folder, old.Name));
        }

        logger.LogInformation("Took the nightly backup {Name}.", name);
        return List().First(backup => backup.Name == name);
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

    private static string DataFolder(IConfiguration configuration)
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
