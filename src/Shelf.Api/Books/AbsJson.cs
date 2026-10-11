using System.Globalization;
using System.Text.Json.Nodes;
using Shelf.Api.Readers;

namespace Shelf.Api.Books;

// A book as Audiobookshelf's apps expect to see one, built in the shapes its own server sends (version 2.37), so the
// apps read Shelf as they read it. Ids are Shelf's, as strings; the one library is the reader's own shelf.
public static class AbsJson
{
    public const string LibraryId = "shelf";
    public const string FolderId = "shelf-books";
    public const string ServerVersion = "2.37.1";

    public static string UserId(int readerId) => $"reader-{readerId}";

    public static string TrackIno(int bookId, int index) => $"{bookId}t{index}";

    public static string EbookIno(int bookId) => $"{bookId}e";

    public static long Ms(DateTimeOffset when) => when.ToUnixTimeMilliseconds();

    // What Shelf knows of a book's recording, gathered once: its tracks and their lengths, its chapters from the start
    // of the whole recording, and where this reader is.
    public sealed record Recording(IReadOnlyList<(int Index, string Title, string FileName, double Length, long Size, string MimeType)> Tracks, IReadOnlyList<(string Title, double Start, double End)> Chapters, double Duration, double CurrentTime);

    public static Recording? RecordingOf(AudioPlan? plan, AudioStore audio, Book book)
    {
        if (plan is null || book.AudioStoredName is not { } stored)
        {
            return null;
        }

        var starts = new List<double>();
        var total = 0.0;
        var tracks = new List<(int, string, string, double, long, string)>();
        foreach (var track in plan.Tracks)
        {
            starts.Add(total);
            var path = audio.TrackPath(stored, track.Index);
            var length = track.Length ?? 0;
            tracks.Add((track.Index, track.Title, path is null ? track.Title : Path.GetFileName(path), length, path is null ? 0 : new FileInfo(path).Length, path is null ? "audio/mpeg" : AudioStore.ContentType(path)));
            total += length;
        }

        var chapters = plan.Chapters.Select(chapter => (chapter.Title, Start: starts[Math.Clamp(chapter.Track, 0, starts.Count - 1)] + chapter.Start)).ToList();
        var placed = chapters.Select((chapter, index) => (chapter.Title, chapter.Start, End: index + 1 < chapters.Count ? chapters[index + 1].Start : total)).ToList();
        var current = (plan.Track < starts.Count ? starts[plan.Track] : 0) + plan.Seconds;
        return new Recording(tracks, placed, total, Math.Min(current, total));
    }

    private static JsonArray Authors(Book book) => new([.. Split(book.Author).Select(name => (JsonNode)new JsonObject { ["id"] = AuthorId(name), ["name"] = name })]);

    public static IEnumerable<string> Split(string? names) => (names ?? "").Split([",", " & ", " and "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string AuthorId(string name) => "author-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(name.ToLowerInvariant())))[..12];

    public static string SeriesId(string name) => "series-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(name.ToLowerInvariant())))[..12];

    private static string IgnorePrefix(string title)
    {
        foreach (var prefix in new[] { "The ", "A ", "An " })
        {
            if (title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && title.Length > prefix.Length)
            {
                return $"{title[prefix.Length..]}, {title[..(prefix.Length - 1)]}";
            }
        }

        return title;
    }

    private static string LastFirst(string author) => string.Join(", ", Split(author).Select(name => name.Split(' ') is { Length: > 1 } parts ? $"{parts[^1]}, {string.Join(' ', parts[..^1])}" : name));

    private static JsonObject Metadata(Book book, bool expanded)
    {
        var metadata = new JsonObject { ["title"] = book.Title };
        if (expanded)
        {
            metadata["subtitle"] = book.Subtitle;
            metadata["authors"] = Authors(book);
            metadata["narrators"] = new JsonArray([.. Split(book.Narrator).Select(name => (JsonNode)name)]);
            metadata["series"] = book.Series is { } series
                ? new JsonArray(new JsonObject { ["id"] = SeriesId(series), ["name"] = series, ["sequence"] = book.SeriesNumber?.ToString(CultureInfo.InvariantCulture) })
                : new JsonArray();
        }
        else
        {
            metadata["titleIgnorePrefix"] = IgnorePrefix(book.Title);
            metadata["subtitle"] = book.Subtitle;
            metadata["authorName"] = book.Author;
            metadata["authorNameLF"] = LastFirst(book.Author);
            metadata["narratorName"] = book.Narrator ?? "";
            metadata["seriesName"] = book.Series is { } series ? (book.SeriesNumber is int number ? $"{series} #{number}" : series) : "";
        }

        metadata["genres"] = new JsonArray([.. book.Tags.Select(tag => (JsonNode)tag.Name)]);
        metadata["publishedYear"] = book.Year?.ToString(CultureInfo.InvariantCulture);
        metadata["publishedDate"] = null;
        metadata["publisher"] = book.Publisher;
        metadata["description"] = null;
        metadata["isbn"] = book.Isbn;
        metadata["asin"] = book.Asin;
        metadata["language"] = book.Language;
        metadata["explicit"] = false;
        metadata["abridged"] = false;
        if (expanded)
        {
            metadata["titleIgnorePrefix"] = IgnorePrefix(book.Title);
            metadata["authorName"] = book.Author;
            metadata["authorNameLF"] = LastFirst(book.Author);
            metadata["narratorName"] = book.Narrator ?? "";
            metadata["seriesName"] = book.Series is { } series ? (book.SeriesNumber is int number ? $"{series} #{number}" : series) : "";
            metadata["descriptionPlain"] = null;
        }

        return metadata;
    }

    // The metadata a playback session carries: the plain kind, without the expanded names.
    public static JsonObject SessionMetadata(Book book)
    {
        var metadata = Metadata(book, expanded: true);
        foreach (var key in new[] { "titleIgnorePrefix", "authorName", "authorNameLF", "narratorName", "seriesName", "descriptionPlain" })
        {
            metadata.Remove(key);
        }

        return metadata;
    }

    private static JsonObject FileMetadata(Book book, string fileName, long size) => new()
    {
        ["filename"] = fileName,
        ["ext"] = Path.GetExtension(fileName),
        ["path"] = $"/shelf/{book.Id}/{fileName}",
        ["relPath"] = fileName,
        ["size"] = size,
        ["mtimeMs"] = Ms(book.AddedAt),
        ["ctimeMs"] = Ms(book.AddedAt),
        ["birthtimeMs"] = Ms(book.AddedAt),
    };

    private static JsonObject AudioFile(Book book, (int Index, string Title, string FileName, double Length, long Size, string MimeType) track) => new()
    {
        ["index"] = track.Index + 1,
        ["ino"] = TrackIno(book.Id, track.Index),
        ["metadata"] = FileMetadata(book, track.FileName, track.Size),
        ["addedAt"] = Ms(book.AddedAt),
        ["updatedAt"] = Ms(book.AddedAt),
        ["trackNumFromMeta"] = null,
        ["discNumFromMeta"] = null,
        ["trackNumFromFilename"] = track.Index + 1,
        ["discNumFromFilename"] = null,
        ["manuallyVerified"] = false,
        ["exclude"] = false,
        ["error"] = null,
        ["format"] = Path.GetExtension(track.FileName).TrimStart('.').ToUpperInvariant(),
        ["duration"] = track.Length,
        ["bitRate"] = track.Length > 0 ? (int)(track.Size * 8 / track.Length) : 0,
        ["language"] = null,
        ["codec"] = Path.GetExtension(track.FileName).TrimStart('.').ToLowerInvariant() switch { "m4a" or "m4b" or "mp4" => "aac", var other => other },
        ["timeBase"] = "1/1000",
        ["channels"] = 2,
        ["channelLayout"] = "stereo",
        ["chapters"] = new JsonArray(),
        ["embeddedCoverArt"] = null,
        ["metaTags"] = new JsonObject { ["tagAlbum"] = book.Title, ["tagArtist"] = book.Author, ["tagTitle"] = track.Title },
        ["mimeType"] = track.MimeType,
    };

    // A track as the player plays it: the audio file, with where it starts in the book and where to fetch it.
    public static JsonObject Track(Book book, Recording recording, int position)
    {
        var track = recording.Tracks[position];
        var json = AudioFile(book, track);
        json["title"] = track.FileName;
        json["startOffset"] = recording.Tracks.Take(position).Sum(item => item.Length);
        json["contentUrl"] = $"/api/items/{book.Id}/file/{TrackIno(book.Id, track.Index)}";
        return json;
    }

    public static JsonArray Chapters(Recording? recording) =>
        new([.. (recording?.Chapters ?? []).Select((chapter, index) => (JsonNode)new JsonObject { ["id"] = index, ["start"] = chapter.Start, ["end"] = chapter.End, ["title"] = chapter.Title })]);

    private static JsonObject? EbookFile(Book book, EbookStore ebooks) =>
        EbookStore.IsEpub(book.EbookStoredName) && ebooks.OpenPath(book.EbookStoredName) is { } path
            ? new JsonObject
            {
                ["ino"] = EbookIno(book.Id),
                ["metadata"] = FileMetadata(book, book.EbookFileName ?? "book.epub", new FileInfo(path).Length),
                ["ebookFormat"] = "epub",
                ["addedAt"] = Ms(book.AddedAt),
                ["updatedAt"] = Ms(book.AddedAt),
            }
            : null;

    private static JsonObject MediaMinified(Book book, Recording? recording, EbookStore ebooks) => new()
    {
        ["id"] = $"media-{book.Id}",
        ["metadata"] = Metadata(book, expanded: false),
        ["coverPath"] = Covers.HasCover(book) ? $"/shelf/{book.Id}/cover" : null,
        ["tags"] = new JsonArray([.. book.Tags.Select(tag => (JsonNode)tag.Name)]),
        ["numTracks"] = recording?.Tracks.Count ?? 0,
        ["numAudioFiles"] = recording?.Tracks.Count ?? 0,
        ["numChapters"] = recording?.Chapters.Count ?? 0,
        ["duration"] = recording?.Duration ?? 0,
        ["size"] = (recording?.Tracks.Sum(track => track.Size) ?? 0) + (EbookFile(book, ebooks)?["metadata"]?["size"]?.GetValue<long>() ?? 0),
        ["ebookFormat"] = EbookStore.IsEpub(book.EbookStoredName) ? "epub" : null,
    };

    private static JsonObject MediaExpanded(Book book, Recording? recording, EbookStore ebooks)
    {
        var media = MediaMinified(book, recording, ebooks);
        media["libraryItemId"] = book.Id.ToString(CultureInfo.InvariantCulture);
        media["metadata"] = Metadata(book, expanded: true);
        media["audioFiles"] = new JsonArray([.. (recording?.Tracks ?? []).Select(track => (JsonNode)AudioFile(book, track))]);
        media["chapters"] = Chapters(recording);
        media["ebookFile"] = EbookFile(book, ebooks);
        media["tracks"] = recording is null ? new JsonArray() : new JsonArray([.. Enumerable.Range(0, recording.Tracks.Count).Select(position => (JsonNode)Track(book, recording, position))]);
        return media;
    }

    private static JsonObject ItemBase(Book book) => new()
    {
        ["id"] = book.Id.ToString(CultureInfo.InvariantCulture),
        ["ino"] = $"{book.Id}i",
        ["oldLibraryItemId"] = null,
        ["libraryId"] = LibraryId,
        ["folderId"] = FolderId,
        ["path"] = $"/shelf/{book.Id}",
        ["relPath"] = book.Id.ToString(CultureInfo.InvariantCulture),
        ["isFile"] = false,
        ["mtimeMs"] = Ms(book.AddedAt),
        ["ctimeMs"] = Ms(book.AddedAt),
        ["birthtimeMs"] = Ms(book.AddedAt),
        ["addedAt"] = Ms(book.AddedAt),
        ["updatedAt"] = Ms(book.AddedAt),
        ["isMissing"] = false,
        ["isInvalid"] = false,
        ["mediaType"] = "book",
    };

    public static JsonObject ItemMinified(Book book, Recording? recording, EbookStore ebooks)
    {
        var item = ItemBase(book);
        item["media"] = MediaMinified(book, recording, ebooks);
        item["numFiles"] = (recording?.Tracks.Count ?? 0) + (EbookStore.IsEpub(book.EbookStoredName) ? 1 : 0);
        item["size"] = item["media"]!["size"]!.GetValue<long>();
        return item;
    }

    public static JsonObject ItemExpanded(Book book, Recording? recording, EbookStore ebooks, JsonObject? progress = null)
    {
        var item = ItemBase(book);
        var media = MediaExpanded(book, recording, ebooks);
        item["media"] = media;
        var files = new JsonArray();
        foreach (var file in media["audioFiles"]!.AsArray())
        {
            files.Add(new JsonObject
            {
                ["ino"] = file!["ino"]!.GetValue<string>(),
                ["metadata"] = file["metadata"]!.DeepClone(),
                ["isSupplementary"] = null,
                ["addedAt"] = Ms(book.AddedAt),
                ["updatedAt"] = Ms(book.AddedAt),
                ["fileType"] = "audio",
            });
        }

        if (media["ebookFile"] is JsonObject ebook)
        {
            files.Add(new JsonObject { ["ino"] = ebook["ino"]!.GetValue<string>(), ["metadata"] = ebook["metadata"]!.DeepClone(), ["isSupplementary"] = false, ["addedAt"] = Ms(book.AddedAt), ["updatedAt"] = Ms(book.AddedAt), ["fileType"] = "ebook" });
        }

        item["libraryFiles"] = files;
        item["lastScan"] = Ms(book.AddedAt);
        item["scanVersion"] = ServerVersion;
        item["numFiles"] = files.Count;
        item["size"] = media["size"]!.GetValue<long>();
        if (progress is not null)
        {
            item["userMediaProgress"] = progress;
        }

        return item;
    }

    // Where a reader is in a book, as Audiobookshelf counts it: seconds from the start, and how much is heard.
    public static JsonObject? Progress(Book book, Recording? recording, int readerId, bool finished, DateTimeOffset? updated)
    {
        if (recording is null && !finished)
        {
            return null;
        }

        var duration = recording?.Duration ?? 0;
        var current = finished ? duration : recording?.CurrentTime ?? 0;
        if (current <= 0 && !finished)
        {
            return null;
        }

        var when = Ms(updated ?? book.AddedAt);
        return new JsonObject
        {
            ["id"] = $"progress-{readerId}-{book.Id}",
            ["userId"] = UserId(readerId),
            ["libraryItemId"] = book.Id.ToString(CultureInfo.InvariantCulture),
            ["episodeId"] = null,
            ["mediaItemId"] = $"media-{book.Id}",
            ["mediaItemType"] = "book",
            ["duration"] = duration,
            ["progress"] = duration > 0 ? Math.Min(1, current / duration) : finished ? 1 : 0,
            ["currentTime"] = current,
            ["isFinished"] = finished,
            ["hideFromContinueListening"] = false,
            ["ebookLocation"] = null,
            ["ebookProgress"] = null,
            ["lastUpdate"] = when,
            ["startedAt"] = when,
            ["finishedAt"] = finished && book.FinishedOn is DateOnly day ? Ms(new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)) : null,
        };
    }

    public static JsonObject Library(int books) => new()
    {
        ["id"] = LibraryId,
        ["name"] = "Shelf",
        ["folders"] = new JsonArray(new JsonObject { ["id"] = FolderId, ["fullPath"] = "/shelf", ["libraryId"] = LibraryId, ["addedAt"] = 0 }),
        ["displayOrder"] = 1,
        ["icon"] = "audiobookshelf",
        ["mediaType"] = "book",
        ["provider"] = "audible",
        ["settings"] = new JsonObject
        {
            ["coverAspectRatio"] = 1,
            ["disableWatcher"] = true,
            ["skipMatchingMediaWithAsin"] = false,
            ["skipMatchingMediaWithIsbn"] = false,
            ["autoScanCronExpression"] = null,
            ["audiobooksOnly"] = false,
            ["epubsAllowScriptedContent"] = false,
            ["hideSingleBookSeries"] = false,
            ["onlyShowLaterBooksInContinueSeries"] = false,
            ["metadataPrecedence"] = new JsonArray("folderStructure", "audioMetatags", "nfoFile", "txtFiles", "opfFile", "absMetadata"),
            ["markAsFinishedPercentComplete"] = null,
            ["markAsFinishedTimeRemaining"] = 10,
        },
        ["lastScan"] = null,
        ["lastScanVersion"] = null,
        ["lastScanMetadataPrecedence"] = null,
        ["createdAt"] = 0,
        ["lastUpdate"] = 0,
        ["stats"] = new JsonObject { ["totalItems"] = books },
    };

    public static JsonObject ServerSettings(string language) => new()
    {
        ["id"] = "server-settings",
        ["scannerFindCovers"] = false,
        ["scannerCoverProvider"] = "google",
        ["scannerParseSubtitle"] = false,
        ["scannerPreferMatchedMetadata"] = false,
        ["scannerDisableWatcher"] = true,
        ["storeCoverWithItem"] = false,
        ["storeMetadataWithItem"] = false,
        ["metadataFileFormat"] = "json",
        ["rateLimitLoginRequests"] = 10,
        ["rateLimitLoginWindow"] = 600000,
        ["allowIframe"] = false,
        ["backupPath"] = "",
        ["backupSchedule"] = false,
        ["backupsToKeep"] = 2,
        ["maxBackupSize"] = 1,
        ["loggerDailyLogsToKeep"] = 7,
        ["loggerScannerLogsToKeep"] = 2,
        ["homeBookshelfView"] = 1,
        ["bookshelfView"] = 1,
        ["podcastEpisodeSchedule"] = "0 * * * *",
        ["sortingIgnorePrefix"] = true,
        ["sortingPrefixes"] = new JsonArray("the", "a", "an"),
        ["chromecastEnabled"] = false,
        ["dateFormat"] = "MM/dd/yyyy",
        ["timeFormat"] = "HH:mm",
        ["language"] = language,
        ["allowedOrigins"] = new JsonArray(),
        ["logLevel"] = 2,
        ["version"] = ServerVersion,
        ["buildNumber"] = 1,
        ["authLoginCustomMessage"] = null,
        ["authActiveAuthMethods"] = new JsonArray("local"),
        ["authOpenIDIssuerURL"] = null,
        ["authOpenIDAuthorizationURL"] = null,
        ["authOpenIDTokenURL"] = null,
        ["authOpenIDUserInfoURL"] = null,
        ["authOpenIDJwksURL"] = null,
        ["authOpenIDLogoutURL"] = null,
        ["authOpenIDTokenSigningAlgorithm"] = "RS256",
        ["authOpenIDButtonText"] = "Login with OpenId",
        ["authOpenIDAutoLaunch"] = false,
        ["authOpenIDAutoRegister"] = false,
        ["authOpenIDMatchExistingBy"] = null,
        ["timeZone"] = "UTC",
    };

    public static JsonObject User(Reader reader, JsonArray progress, JsonArray bookmarks, string? accessToken, string? refreshToken) => new()
    {
        ["id"] = UserId(reader.Id),
        ["username"] = reader.Name,
        ["email"] = reader.Email,
        ["type"] = reader.IsAdmin ? "admin" : "user",
        ["token"] = accessToken,
        ["mediaProgress"] = progress,
        ["seriesHideFromContinueListening"] = new JsonArray(),
        ["bookmarks"] = bookmarks,
        ["isActive"] = true,
        ["isLocked"] = false,
        ["lastSeen"] = Ms(DateTimeOffset.UtcNow),
        ["createdAt"] = Ms(reader.CreatedAt),
        ["permissions"] = new JsonObject
        {
            ["download"] = true,
            ["update"] = false,
            ["delete"] = false,
            ["upload"] = false,
            ["createEreader"] = false,
            ["accessAllLibraries"] = true,
            ["accessAllTags"] = true,
            ["accessExplicitContent"] = true,
            ["selectedTagsNotAccessible"] = false,
        },
        ["librariesAccessible"] = new JsonArray(),
        ["itemTagsSelected"] = new JsonArray(),
        ["hasOpenIDLink"] = false,
        ["refreshToken"] = refreshToken,
        ["accessToken"] = accessToken,
    };

    public static JsonObject Bookmark(int bookId, string title, double time, DateTimeOffset created) => new()
    {
        ["libraryItemId"] = bookId.ToString(CultureInfo.InvariantCulture),
        ["title"] = title,
        ["time"] = time,
        ["createdAt"] = Ms(created),
    };
}
