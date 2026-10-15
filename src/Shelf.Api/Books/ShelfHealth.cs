using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public enum HealthState
{
    Good,
    Off,
    Look,
    Wrong,
}

// One thing about the server: how it stands in plain words, what to do about it, and the detail behind it for whoever
// looks after the machine.
public sealed record HealthItem(string Area, string Name, HealthState State, string Says, string? Fix = null, string? Detail = null)
{
    // Where the documentation explains it, as a guide and a section of it (see Docs).
    public DocsPlace? Help { get; init; }
}

public sealed record HealthReport(DateTimeOffset At, string Version, HealthItem[] Items, Heartbeat[] Jobs)
{
    public HealthState Worst => Items.Select(item => item.State).DefaultIfEmpty(HealthState.Good).Max();
}

// Everything an admin may need to know is well or isn't, in one place: backups, stored files, disk space, background
// work, and the services Shelf talks to. Worked out when asked; nothing here is kept.
public sealed class ShelfHealth(
    BackupSchedule schedule,
    BackupCheck backups,
    OffsiteBackup offsite,
    FileChecker files,
    ShelfJobs jobs,
    ShelfEvents events,
    OcrTools ocr,
    IEmailSender email,
    IRecapWriter recaps,
    ReleaseWatch releases,
    IConfiguration configuration)
{
    public async Task<HealthReport> ReportAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var items = new List<HealthItem>();
        var now = DateTimeOffset.UtcNow;
        var version = typeof(ShelfHealth).Assembly.GetName().Version?.ToString(3) ?? "";

        // The app and its database.
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        items.Add(new HealthItem(T("Shelf"), T("This server"), pending.Count == 0 ? HealthState.Good : HealthState.Wrong,
            T("Shelf {0}, running since {1}.", version, Heartbeats.Started.Medium()),
            pending.Count == 0 ? null : T("Restart Shelf so it can bring its database up to date."),
            $"{db.Database.ProviderName}; {Environment.OSVersion}; .NET {Environment.Version}; pending migrations: {(pending.Count == 0 ? "none" : string.Join(", ", pending))}"));
        items.Add(new HealthItem(T("Shelf"), T("Updates"),
            releases.Newer is not null ? HealthState.Look : !releases.Enabled ? HealthState.Off : releases.Problem is not null ? HealthState.Look : HealthState.Good,
            releases.Newer is { } newer ? T("Shelf {0} is out; this server runs {1}.", newer.Version, version)
                : !releases.Enabled ? T("Shelf doesn't look for newer releases (Updates:Check is false).")
                : releases.CheckedAt is null ? T("Shelf hasn't looked for a newer release yet.")
                : releases.Problem is not null ? T("Shelf couldn't ask whether a newer release is out.")
                : T("This is the newest Shelf."),
            releases.Newer is not null ? T("Pull the new image and start the container again; the data folder is kept.") : null,
            releases.Newer?.Url ?? releases.Problem));
        if (!db.IsPostgres && db.Database.GetDbConnection().DataSource is { Length: > 0 } file && File.Exists(file))
        {
            items.Add(new HealthItem(T("Shelf"), T("Database"), HealthState.Good, T("The database takes {0}.", Localization.Counts.Size(new FileInfo(file).Length)), null, file));
        }

        // Backups: taken, proved, and sent away.
        if (!schedule.Enabled)
        {
            items.Add(new HealthItem(T("Backups"), T("Nightly backups"), HealthState.Wrong, T("Nightly backups are off."),
                T("Set Backup:Enabled to true, or be sure something else copies the data folder each night."), "Backup:Enabled=false"));
        }
        else
        {
            var taken = backups.List();
            var latest = taken.FirstOrDefault();
            if (latest is null)
            {
                items.Add(new HealthItem(T("Backups"), T("Nightly backups"), HealthState.Look, T("No backup has been taken yet."),
                    T("Take one now from Readers, under Nightly backups."), schedule.Folder));
            }
            else
            {
                var age = now - latest.TakenAt;
                var state = age > TimeSpan.FromHours(36) ? HealthState.Wrong : latest.Verdict is { Ok: false } ? HealthState.Wrong : latest.Verdict is null ? HealthState.Look : HealthState.Good;
                var says = latest.Verdict switch
                {
                    { Ok: true } => T("The last backup, {0}, was proved to restore.", latest.TakenAt.Medium()),
                    { Ok: false } bad => T("The last backup, {0}, would not restore: {1}", latest.TakenAt.Medium(), bad.Problem ?? ""),
                    _ => T("The last backup was taken {0}, and hasn't been proved yet.", latest.TakenAt.Medium()),
                };
                var fix = age > TimeSpan.FromHours(36) ? T("No backup for over a day: see the server's log for why, and check there is room in the backup folder.")
                    : latest.Verdict is { Ok: false } ? T("Take a backup now and check that it restores; if it fails again, the log says why.")
                    : latest.Verdict is null ? T("Check that it restores, from Readers, under Nightly backups.") : null;
                items.Add(new HealthItem(T("Backups"), T("Nightly backups"), state, says, fix, $"{schedule.Folder}; {latest.Name}; {Localization.Counts.Size(latest.Bytes)}"));
            }

            if (offsite.CopyFolder is null && offsite.S3Description is null)
            {
                items.Add(new HealthItem(T("Backups"), T("Copies elsewhere"), HealthState.Look, T("Backups are kept on this server's disk only."),
                    T("Set Backup:CopyTo or Backup:S3 so a failed disk doesn't take the backups with it.")));
            }
            else
            {
                var failed = offsite.Last.Where(result => !result.Sent).ToList();
                items.Add(new HealthItem(T("Backups"), T("Copies elsewhere"), failed.Count == 0 ? HealthState.Good : HealthState.Wrong,
                    failed.Count == 0 ? T("Each backup is copied to: {0}.", string.Join(", ", new[] { offsite.CopyFolder, offsite.S3Description }.OfType<string>())) : T("The last copy to {0} failed.", failed[0].Where),
                    failed.Count == 0 ? null : T("Check the address and the keys in the Backup settings, and that the place has room."),
                    string.Join("; ", offsite.Last.Select(result => $"{result.Where}: {(result.Sent ? "sent" : result.Problem)} at {result.At:u}"))));
            }
        }

        // Stored files, as the weekly check last found them.
        var fileHealth = await FileCheck.HealthAsync(db, files, backups, cancellationToken);
        var allProblems = await db.FileRecords.AsNoTracking().CountAsync(record => record.Problem != null, cancellationToken);
        items.Add(new HealthItem(T("Files"), T("Stored files"),
            allProblems > 0 ? HealthState.Wrong : fileHealth.CheckedAt is null || now - fileHealth.CheckedAt > TimeSpan.FromDays(10) ? HealthState.Look : HealthState.Good,
            allProblems > 0 ? T("{0} stored files are missing, changed, or can't be read.", allProblems.ToString("N0"))
                : fileHealth.CheckedAt is DateTimeOffset at ? T("Every stored file was last read through and checked on {0}.", at.Medium()) : T("The stored files have not been checked yet."),
            allProblems > 0 ? T("Readers see theirs under Tidy up; bring each back from a backup.") : fileHealth.CheckedAt is null ? T("Check every file now, from Tidy up.") : null,
            T("{0} files known", fileHealth.Files.ToString("N0"))));

        // Room on the disk the data lives on.
        var data = Path.GetFullPath(BackupSchedule.DataFolder(configuration));
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(data) ?? data);
            var free = drive.AvailableFreeSpace;
            var share = drive.TotalSize > 0 ? (double)free / drive.TotalSize : 1;
            items.Add(new HealthItem(T("Files"), T("Disk space"), free < 1L << 30 || share < 0.03 ? HealthState.Wrong : share < 0.1 ? HealthState.Look : HealthState.Good,
                T("{0} free of {1}.", Localization.Counts.Size(free), Localization.Counts.Size(drive.TotalSize)),
                share < 0.1 ? T("Make room, or move the data folder to a larger disk; uploads and backups stop when it fills.") : null, data));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            items.Add(new HealthItem(T("Files"), T("Disk space"), HealthState.Look, T("How much room is left can't be read."), null, ex.Message));
        }

        // Work in the background.
        var recent = jobs.All.Where(job => now - job.Started < TimeSpan.FromDays(7)).ToList();
        var failedLines = recent.Sum(job => ImportJobs.Summarize(job, now).Failed);
        items.Add(new HealthItem(T("Background work"), T("Imports"), failedLines > 0 ? HealthState.Look : HealthState.Good,
            recent.Count == 0 ? T("No imports this week.") : T("{0} imports this week; {1} books could not be brought.", recent.Count, failedLines),
            failedLines > 0 ? T("Each reader sees what failed, and why, under Activity.") : null,
            string.Join("; ", recent.Take(10).Select(job => $"{job.Title(job.Finished)} {job.Started:u}"))));
        items.Add(new HealthItem(T("Background work"), T("Events"), events.Idle ? HealthState.Good : HealthState.Look,
            events.Idle ? T("Notifications, webhooks, and rules are all caught up.") : T("Notifications, webhooks, and rules are still being sent.")));

        // What Shelf talks to.
        var hooks = await db.Webhooks.IgnoreQueryFilters().AsNoTracking().Select(hook => new { hook.Enabled, hook.Failures, hook.LastStatus, hook.Url }).ToListAsync(cancellationToken);
        var failing = hooks.Where(hook => hook.Failures > 0).ToList();
        if (hooks.Count > 0)
        {
            items.Add(new HealthItem(T("Connections"), T("Webhooks"), failing.Count == 0 ? HealthState.Good : HealthState.Look,
                failing.Count == 0 ? T("{0} webhooks, all answering.", hooks.Count) : T("{0} of {1} webhooks are failing.", failing.Count, hooks.Count),
                failing.Count == 0 ? null : T("Their readers see which, under Notifications; one failing {0} times in a row is turned off.", Webhooks.MaxFailures),
                string.Join("; ", failing.Select(hook => $"{new Uri(hook.Url).Host}: {hook.LastStatus?.ToString() ?? "no answer"} ×{hook.Failures}"))));
        }

        var links = await db.ShelfLinks.IgnoreQueryFilters().AsNoTracking().Where(link => link.Active).Select(link => new { link.Name, link.LastSeenAt }).ToListAsync(cancellationToken);
        if (links.Count > 0)
        {
            var quiet = links.Where(link => link.LastSeenAt is null || now - link.LastSeenAt > TimeSpan.FromDays(3)).ToList();
            items.Add(new HealthItem(T("Connections"), T("Linked shelves"), quiet.Count == 0 ? HealthState.Good : HealthState.Look,
                quiet.Count == 0 ? T("{0} linked shelves, all heard from lately.", links.Count) : T("Not heard from in three days: {0}.", string.Join(", ", quiet.Select(link => link.Name))),
                quiet.Count == 0 ? null : T("Check the other shelf is running and can reach this one."),
                string.Join("; ", links.Select(link => $"{link.Name}: {link.LastSeenAt?.ToString("u") ?? "never"}"))));
        }

        if (email.Enabled && email.Problem is { } mailProblem)
        {
            items.Add(new HealthItem(T("Connections"), T("Email"), HealthState.Wrong, T("The last email did not go: {0}", mailProblem),
                T("Check Email:Host, Email:Port, Email:Security, Email:User, and Email:Password, then send a test email from Account."),
                $"{configuration["Email:Host"]}:{configuration.GetValue("Email:Port", 587)}, {configuration["Email:Security"] ?? "auto"}"));
        }
        else
        {
            items.Add(new HealthItem(T("Connections"), T("Email"), email.Enabled ? HealthState.Good : HealthState.Off,
                email.Enabled ? T("Email is set up, for password resets and notifications.") : T("Email is not set up: readers can't reset a forgotten password by email."),
                email.Enabled ? null : T("Set Email:Host and Email:From."), email.Enabled ? configuration["Email:Host"] : null));
        }
        // Turnstile: off, half set up (which leaves the forms unguarded), or on.
        var siteKey = !string.IsNullOrWhiteSpace(configuration["Turnstile:SiteKey"]);
        var secretKey = !string.IsNullOrWhiteSpace(configuration["Turnstile:SecretKey"]);
        var hosts = Readers.Turnstile.Hostnames(configuration);
        items.Add((siteKey, secretKey, hosts.Count > 0) switch
        {
            (false, false, _) => new HealthItem(T("Connections"), T("Turnstile"), HealthState.Off,
                T("Turnstile is off: signing in, signing up, and forgotten passwords have no check for scripts beyond the limit on attempts."),
                T("Set Turnstile:SiteKey and Turnstile:SecretKey from a Cloudflare Turnstile widget of your own.")),
            (true, true, true) => new HealthItem(T("Connections"), T("Turnstile"), HealthState.Good,
                T("Turnstile guards signing in, signing up, and forgotten passwords."), null, string.Join(", ", hosts)),
            (true, true, false) => new HealthItem(T("Connections"), T("Turnstile"), HealthState.Wrong,
                T("Turnstile has its keys but no address to expect answers from, so it is off."),
                T("Set Turnstile:Hostnames to the shelf's host names, or Email:PublicAddress.")),
            _ => new HealthItem(T("Connections"), T("Turnstile"), HealthState.Wrong,
                T("Turnstile has only one of its two keys, so it is off."),
                T("Set both Turnstile:SiteKey and Turnstile:SecretKey.")),
        });
        items.Add(new HealthItem(T("Connections"), T("Reading scanned pages"), ocr.Available ? HealthState.Good : HealthState.Off,
            ocr.Available ? T("Tesseract is installed: scanned PDFs and photos of spines can be read.") : T("Tesseract isn't installed: scanned PDFs and photos of spines can't be read."),
            ocr.Available ? null : T("Install tesseract-ocr and poppler-utils, as the Docker image has them."), ocr.Tesseract));
        items.Add(new HealthItem(T("Connections"), T("Recaps"), recaps.Ready ? HealthState.Good : HealthState.Off,
            recaps.Ready ? T("Recaps, and questions in your own words, can ask Anthropic.") : T("Recaps are off: no Anthropic key is set."),
            recaps.Ready ? null : T("Set Recap:ApiKey to turn them on; everything else works without it.")));

        // Each thing that can need fixing links to how to fix it.
        var help = new Dictionary<string, DocsPlace>
        {
            [T("Updates")] = new("install", "updating"),
            [T("Nightly backups")] = new("configuration", "nightly-backups"),
            [T("Copies elsewhere")] = new("configuration", "nightly-backups"),
            [T("Stored files")] = new("backups", "checking-files"),
            [T("Disk space")] = new("server", "disk-space"),
            [T("Email")] = new("configuration", "email"),
            [T("Turnstile")] = new("configuration", "cloudflare-turnstile"),
            [T("Reading scanned pages")] = new("server", "ocr-for-scanned-pdfs"),
        };
        return new HealthReport(now, version, [.. items.Select(item => help.TryGetValue(item.Name, out var where) ? item with { Help = where } : item)], [.. Heartbeats.All]);
    }

    public static string StateLabel(HealthState state) => state switch
    {
        HealthState.Good => T("Well"),
        HealthState.Off => T("Off"),
        HealthState.Look => T("Worth a look"),
        _ => T("Needs you"),
    };

    public static string JobLabel(string job) => job switch
    {
        nameof(BackupSchedule) => T("Nightly backups"),
        nameof(FileChecker) => T("File check"),
        nameof(AutomationRunner) => T("Rules for overdue loans"),
        nameof(Notices) => T("Loan reminders"),
        nameof(SeriesWatch) => T("New books in your series"),
        nameof(ReleaseWatch) => T("Looking for a newer Shelf"),
        _ => job,
    };
}
