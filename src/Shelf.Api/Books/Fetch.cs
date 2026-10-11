using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

public enum FetchKind
{
    Ebook,
    Audiobook,
}

public enum GrabState
{
    // Handed to the download client.
    Sent,
    Downloading,
    // Downloaded; being brought onto the book.
    Importing,
    Done,
    Failed,
}

public enum IndexerKind
{
    Jackett,
    Prowlarr,
}

public enum TorrentClientKind
{
    Deluge,
    QBittorrent,
}

// How the shelf finds and fetches books, set by an admin: Jackett or Prowlarr for searching the indexers, Deluge or
// qBittorrent for torrents, and SABnzbd for Usenet. One row for the whole shelf; its passwords and keys are sealed.
public sealed class FetchSetting
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
    public IndexerKind IndexerKind { get; set; }
    public string? IndexerUrl { get; set; }
    public string? SealedIndexerKey { get; set; }
    public TorrentClientKind TorrentKind { get; set; }
    public string? TorrentUrl { get; set; }

    // qBittorrent's user name; Deluge's web interface asks for its password alone.
    public string? TorrentUser { get; set; }
    public string? SealedTorrentPassword { get; set; }
    public string? SabUrl { get; set; }
    public string? SealedSabKey { get; set; }
    public string Category { get; set; } = "shelf";

    // Where the download client's finished files are, as it names the folder and as Shelf sees it.
    public string? RemotePath { get; set; }
    public string? LocalPath { get; set; }

    // Kinds of file in order of liking, the first best.
    public string EbookFormats { get; set; } = "epub,azw3,mobi,pdf";
    public string AudioFormats { get; set; } = "m4b,mp3";
    public int MinSeeders { get; set; } = 1;

    // Once a day, look for want-to-read books that have no file, and fetch the best match for each.
    public bool SearchWanted { get; set; }
    public FetchKind WantedKind { get; set; } = FetchKind.Ebook;
    public int MostPerDay { get; set; } = 10;
    public DateTimeOffset? WantedSearchedAt { get; set; }
}

// One download for one book: what was chosen, where the client is with it, and how it ended.
public sealed class FetchGrab
{
    public int Id { get; set; }
    public int ReaderId { get; set; }
    public int BookId { get; set; }
    public FetchKind Kind { get; set; }
    public required string Title { get; set; }
    public string? Indexer { get; set; }
    public required string Protocol { get; set; }
    public string? DownloadId { get; set; }
    public GrabState State { get; set; }
    public double Progress { get; set; }
    public string? Path { get; set; }
    public string? Problem { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// A release an indexer offers, as Jackett or Prowlarr gives it.
public sealed record Release(
    string Guid,
    string Title,
    long Size,
    int? Seeders,
    string Protocol,
    string? DownloadUrl,
    string? MagnetUrl,
    string? Indexer,
    string? InfoUrl,
    int[] Categories,
    DateTimeOffset? Published);

// A release weighed for a book: whether it is that book at all, the kind of file it holds, and how well it suits.
public sealed record ScoredRelease(Release Release, bool Matches, string? Format, int Score, string? Why);

public static class FetchRules
{
    private static readonly string[] Small = ["the", "a", "an", "of", "and", "or", "in", "on", "to", "de", "la", "le", "el", "der", "die", "das"];
    private static readonly string[] EbookWords = ["epub", "azw3", "azw", "mobi", "pdf", "cbz", "cbr", "kepub", "fb2"];
    private static readonly string[] AudioWords = ["m4b", "mp3", "flac", "aac", "ogg", "opus", "m4a"];

    public static bool IsEbookCategory(int category) => category is >= 7000 and < 8000 and not 7010;

    public static bool IsAudioCategory(int category) => category == 3030;

    public static string[] Formats(string list) =>
        [.. list.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).Select(format => format.Trim().TrimStart('.').ToLowerInvariant())];

    // The words of a book's title that matter: the main title, before any subtitle, without the small words.
    public static string[] TitleWords(string title)
    {
        var main = title.Split([':', '(', '['], 2)[0];
        var words = Tidy.Plain(main).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = words.Where(word => !Small.Contains(word)).ToArray();
        return kept.Length > 0 ? kept : words;
    }

    public static string LastName(string author)
    {
        var first = author.Split(['&', ';'], 2)[0].Split(" and ", 2, StringSplitOptions.None)[0].Trim();
        if (first.Contains(',', StringComparison.Ordinal))
        {
            return Tidy.Plain(first.Split(',')[0]).Split(' ').LastOrDefault() ?? "";
        }

        return Tidy.Plain(first).Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
    }

    // Whether a release is this book, and of the kind asked for, and how much it is liked: the format first, then
    // how many seed it (for a torrent), then how recent.
    public static ScoredRelease Score(Release release, string title, string author, FetchKind kind, FetchSetting settings)
    {
        var words = Tidy.Plain(release.Title).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var titleWords = TitleWords(title);
        var last = LastName(author);
        if (titleWords.Length == 0 || !titleWords.All(words.Contains))
        {
            return new(release, false, null, 0, "the title is not in it");
        }

        if (last.Length > 0 && !words.Contains(last))
        {
            return new(release, false, null, 0, "the author is not in it");
        }

        var wanted = kind == FetchKind.Ebook ? Formats(settings.EbookFormats) : Formats(settings.AudioFormats);
        var kindWords = kind == FetchKind.Ebook ? EbookWords : AudioWords;
        var otherWords = kind == FetchKind.Ebook ? AudioWords : EbookWords;
        var format = wanted.FirstOrDefault(words.Contains) ?? kindWords.FirstOrDefault(words.Contains);
        var rightCategory = release.Categories.Length == 0 || release.Categories.Any(kind == FetchKind.Ebook ? IsEbookCategory : IsAudioCategory);
        if (!rightCategory || (format is null && otherWords.Any(words.Contains)))
        {
            return new(release, false, format, 0, kind == FetchKind.Ebook ? "it is not an e-book" : "it is not an audiobook");
        }

        var most = kind == FetchKind.Ebook ? EbookStore.MaxBytes : AudioStore.MaxBytes;
        if (release.Size > most)
        {
            return new(release, false, format, 0, "it is larger than the shelf takes");
        }

        if (release.Protocol == "torrent" && (release.Seeders ?? 0) < Math.Max(0, settings.MinSeeders))
        {
            return new(release, false, format, 0, "too few seed it");
        }

        var score = 100;
        var rank = format is null ? -1 : Array.IndexOf(wanted, format);
        score += rank < 0 ? 0 : (wanted.Length - rank) * 40;
        if (release.Protocol == "torrent")
        {
            score += (int)Math.Min(60, Math.Log2(1 + (release.Seeders ?? 0)) * 10);
        }

        if (release.Published is { } published)
        {
            score += Math.Max(0, 20 - (int)(DateTimeOffset.UtcNow - published).TotalDays / 180);
        }

        return new(release, true, format, score, null);
    }

    public static IReadOnlyList<ScoredRelease> Rank(IEnumerable<Release> releases, string title, string author, FetchKind kind, FetchSetting settings) =>
        [.. releases.Select(release => Score(release, title, author, kind, settings))
            .OrderByDescending(item => item.Matches)
            .ThenByDescending(item => item.Score)
            .ThenByDescending(item => item.Release.Seeders ?? 0)];

    // A path as the download client names it, as Shelf sees it.
    public static string MapPath(string path, FetchSetting settings)
    {
        if (settings.RemotePath is { Length: > 0 } remote && settings.LocalPath is { Length: > 0 } local
            && path.StartsWith(remote.TrimEnd('/', '\\'), StringComparison.Ordinal))
        {
            return local.TrimEnd('/', '\\') + path[remote.TrimEnd('/', '\\').Length..].Replace('\\', '/');
        }

        return path;
    }

    public static async Task<FetchSetting> SettingsAsync(ShelfDb db, CancellationToken cancellationToken = default) =>
        await db.FetchSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken) ?? new FetchSetting();

    public static string Size(long bytes) => Localization.Counts.Size(bytes);

    public static string Search(string title, string author) =>
        string.Join(' ', TitleWords(title).Take(6).Append(LastName(author)).Where(word => word.Length > 0)).ToString(CultureInfo.InvariantCulture);
}
