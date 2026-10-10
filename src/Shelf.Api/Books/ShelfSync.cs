using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

public static class ShelfSync
{
    public static string Key(string? isbn, string title, string author)
    {
        var normalized = BookRules.NormalizeIsbn(isbn);
        var raw = normalized is not null
            ? "isbn\n" + normalized
            : "book\n" + title.Trim().ToLowerInvariant() + "\n" + author.Trim().ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    public static bool SameBook(string? leftIsbn, string leftTitle, string leftAuthor, string? rightIsbn, string rightTitle, string rightAuthor)
    {
        var left = BookRules.NormalizeIsbn(leftIsbn);
        var right = BookRules.NormalizeIsbn(rightIsbn);
        if (left is not null && right is not null)
        {
            return left == right;
        }

        return string.Equals(leftTitle.Trim(), rightTitle.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(leftAuthor.Trim(), rightAuthor.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<SyncCatalog> CatalogAsync(
        ShelfDb db,
        EbookStore ebooks,
        AudioStore audio,
        CancellationToken cancellationToken)
    {
        var books = await db.Books.AsNoTracking().Include(book => book.Highlights).ToListAsync(cancellationToken);
        var entries = new List<SyncBook>();
        foreach (var book in books)
        {
            var ebook = await DescribeEbookAsync(ebooks, book, cancellationToken);
            var recording = await DescribeAudioAsync(audio, book, cancellationToken);
            if (ebook is null && recording is null)
            {
                continue;
            }

            entries.Add(new SyncBook(
                Key(book.Isbn, book.Title, book.Author),
                book.Title,
                book.Author,
                book.Isbn,
                book.EbookChapter,
                book.AudioTrack,
                book.AudioSeconds,
                ebook,
                recording,
                book.Highlights
                    .OrderBy(mark => mark.Id)
                    .Select(mark => new SyncHighlight(mark.ChapterIndex, mark.Text, mark.Note, mark.Prefix, mark.Suffix))
                    .ToArray()));
        }

        return new SyncCatalog(entries.ToArray());
    }

    public static async Task<IReadOnlyList<string>> FromShelfAsync(
        HttpClient client,
        string? address,
        string? key,
        ShelfDb db,
        EbookStore ebooks,
        AudioStore audio,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(address)
            || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return [Localization.Words.T("Enter an address that starts with http:// or https://.")];
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            return [Localization.Words.T("Enter the key made on the other shelf.")];
        }

        client.BaseAddress = new Uri($"{uri.Scheme}://{uri.Authority}/");
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key.Trim());
        try
        {
            using var answer = await client.GetAsync("books/sync", cancellationToken);
            if (answer.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return [Localization.Words.T("The other shelf did not accept that key.")];
            }

            var catalog = answer.IsSuccessStatusCode
                ? await answer.Content.ReadFromJsonAsync<SyncCatalog>(cancellationToken)
                : null;
            if (catalog?.Books is null)
            {
                return [Localization.Words.T("That address is not a shelf.")];
            }

            return await ExchangeAsync(
                db,
                ebooks,
                audio,
                catalog,
                async (book, kind, token) => await DownloadAsync(client, book, kind, token),
                async (offer, token) => await EnsureAsync(client, offer, token),
                async (key, kind, stream, fileName, token) => await UploadAsync(client, key, kind, stream, fileName, token),
                async (key, progress, token) => await client.PutAsJsonAsync($"books/sync/{key}/progress", progress, token),
                cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or Polly.ExecutionRejectedException or TaskCanceledException or JsonException)
        {
            return [Localization.Words.T("That shelf could not be reached.")];
        }
    }

    public static async Task<IReadOnlyList<string>> ExchangeAsync(
        ShelfDb db,
        EbookStore ebooks,
        AudioStore audio,
        SyncCatalog remote,
        Func<SyncBook, string, CancellationToken, Task<Stream?>> download,
        Func<SyncOffer, CancellationToken, Task<string?>> ensureRemote,
        Func<string, string, Stream, string, CancellationToken, Task<int>> upload,
        Func<string, SyncProgress, CancellationToken, Task> pushProgress,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        var locals = await db.Books.Include(book => book.Highlights).ToListAsync(cancellationToken);
        foreach (var incoming in remote.Books)
        {
            var local = locals.FirstOrDefault(book => SameBook(book.Isbn, book.Title, book.Author, incoming.Isbn, incoming.Title, incoming.Author));
            var created = false;
            if (local is null)
            {
                if (string.IsNullOrWhiteSpace(incoming.Title) || string.IsNullOrWhiteSpace(incoming.Author))
                {
                    continue;
                }

                local = new Book
                {
                    Title = incoming.Title.Trim(),
                    Author = incoming.Author.Trim(),
                    Isbn = BookRules.NormalizeIsbn(incoming.Isbn),
                    AddedAt = DateTimeOffset.UtcNow,
                };
                created = true;
            }
            else if (local.Isbn is null)
            {
                local.Isbn = BookRules.NormalizeIsbn(incoming.Isbn);
            }

            var brought = false;
            var moved = false;
            if (incoming.Ebook is not null)
            {
                var hash = await ebooks.HashAsync(local.EbookStoredName, cancellationToken);
                if (hash is null)
                {
                    if (incoming.Ebook.Bytes > EbookStore.MaxBytes)
                    {
                        lines.Add(Localization.Words.T("The e-book of {0} is too large to bring.", incoming.Title));
                    }
                    else if (await TakeEbookAsync(ebooks, local, incoming, download, cancellationToken))
                    {
                        lines.Add(Localization.Words.T("Brought the e-book of {0}.", incoming.Title));
                        brought = true;
                        CopyHighlights(local, incoming.Highlights);
                    }
                    else
                    {
                        lines.Add(Localization.Words.T("Could not bring the e-book of {0}.", incoming.Title));
                    }
                }
                else if (hash == incoming.Ebook.Sha256)
                {
                    moved = MovePlace(local, incoming);
                }
                else
                {
                    lines.Add(Localization.Words.T("Kept the e-book already on {0}.", incoming.Title));
                }
            }

            if (incoming.Audio is not null)
            {
                var hash = await audio.HashAsync(local.AudioStoredName, cancellationToken);
                if (hash is null)
                {
                    if (incoming.Audio.Bytes > AudioStore.MaxBytes)
                    {
                        lines.Add(Localization.Words.T("The audiobook of {0} is too large to bring.", incoming.Title));
                    }
                    else if (await TakeAudioAsync(audio, local, incoming, download, cancellationToken))
                    {
                        lines.Add(Localization.Words.T("Brought the audiobook of {0}.", incoming.Title));
                        brought = true;
                    }
                    else
                    {
                        lines.Add(Localization.Words.T("Could not bring the audiobook of {0}.", incoming.Title));
                    }
                }
                else if (hash == incoming.Audio.Sha256)
                {
                    moved = MoveListening(local, incoming) || moved;
                }
                else
                {
                    lines.Add(Localization.Words.T("Kept the audiobook already on {0}.", incoming.Title));
                }
            }

            if (moved)
            {
                lines.Add(Localization.Words.T("The place in {0} moved ahead.", incoming.Title));
            }

            if (created && brought)
            {
                db.Books.Add(local);
                locals.Add(local);
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        foreach (var local in locals)
        {
            var match = remote.Books.FirstOrDefault(item => SameBook(local.Isbn, local.Title, local.Author, item.Isbn, item.Title, item.Author));
            var ebookHash = await ebooks.HashAsync(local.EbookStoredName, cancellationToken);
            var audioHash = await audio.HashAsync(local.AudioStoredName, cancellationToken);
            var remoteKey = match?.Key;
            var sentEbook = false;
            var sentAudio = false;
            if (ebookHash is not null && match?.Ebook is null)
            {
                remoteKey ??= await ensureRemote(new SyncOffer(local.Title, local.Author, local.Isbn), cancellationToken);
                if (remoteKey is not null && await SendEbookAsync(ebooks, local, remoteKey, upload, cancellationToken))
                {
                    lines.Add(Localization.Words.T("Sent the e-book of {0}.", local.Title));
                    sentEbook = true;
                }
                else
                {
                    lines.Add(Localization.Words.T("Could not send the e-book of {0}.", local.Title));
                }
            }

            if (audioHash is not null && match?.Audio is null)
            {
                remoteKey ??= await ensureRemote(new SyncOffer(local.Title, local.Author, local.Isbn), cancellationToken);
                if (remoteKey is not null && await SendAudioAsync(audio, local, remoteKey, upload, cancellationToken))
                {
                    lines.Add(Localization.Words.T("Sent the audiobook of {0}.", local.Title));
                    sentAudio = true;
                }
                else
                {
                    lines.Add(Localization.Words.T("Could not send the audiobook of {0}.", local.Title));
                }
            }

            if (remoteKey is null)
            {
                continue;
            }

            var sameEbook = sentEbook || (ebookHash is not null && ebookHash == match?.Ebook?.Sha256);
            var sameAudio = sentAudio || (audioHash is not null && audioHash == match?.Audio?.Sha256);
            if (!sameEbook && !sameAudio)
            {
                continue;
            }

            try
            {
                await pushProgress(
                    remoteKey,
                    new SyncProgress(
                        sameEbook ? ebookHash : null,
                        sameEbook ? local.EbookChapter : null,
                        sameAudio ? audioHash : null,
                        sameAudio ? local.AudioTrack : null,
                        sameAudio ? local.AudioSeconds : null,
                        sameEbook
                            ? local.Highlights.Select(mark => new SyncHighlight(mark.ChapterIndex, mark.Text, mark.Note, mark.Prefix, mark.Suffix)).ToArray()
                            : []),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or Polly.ExecutionRejectedException or TaskCanceledException)
            {
            }
        }

        if (lines.Count == 0)
        {
            lines.Add(Localization.Words.T("Nothing new to carry across."));
        }

        return lines;
    }

    public static bool ApplyProgress(Book book, string? ebookHash, string? audioHash, SyncProgress progress)
    {
        var moved = false;
        if (progress.EbookSha256 is not null
            && progress.EbookSha256 == ebookHash
            && progress.EbookChapter is int chapter
            && chapter >= 0
            && chapter > (book.EbookChapter ?? -1)
            && (book.EbookChapter is not null || chapter > 0))
        {
            book.EbookChapter = chapter;
            moved = true;
        }

        if (progress.AudioSha256 is not null
            && progress.AudioSha256 == audioHash
            && progress.AudioTrack is int track
            && progress.AudioSeconds is int seconds
            && track >= 0
            && seconds >= 0
            && Further(track, seconds, book.AudioTrack, book.AudioSeconds))
        {
            book.AudioTrack = track;
            book.AudioSeconds = seconds;
            moved = true;
        }

        if (progress.EbookSha256 is not null && progress.EbookSha256 == ebookHash && CopyHighlights(book, progress.Highlights))
        {
            moved = true;
        }

        return moved;
    }

    private static async Task<SyncFile?> DescribeEbookAsync(EbookStore ebooks, Book book, CancellationToken cancellationToken)
    {
        var hash = await ebooks.HashAsync(book.EbookStoredName, cancellationToken);
        var path = ebooks.OpenPath(book.EbookStoredName);
        if (hash is null || path is null || string.IsNullOrWhiteSpace(book.EbookFileName))
        {
            return null;
        }

        return new SyncFile(book.EbookFileName, new FileInfo(path).Length, hash);
    }

    private static async Task<SyncFile?> DescribeAudioAsync(AudioStore audio, Book book, CancellationToken cancellationToken)
    {
        var hash = await audio.HashAsync(book.AudioStoredName, cancellationToken);
        if (hash is null || string.IsNullOrWhiteSpace(book.AudioFileName))
        {
            return null;
        }

        long bytes = 0;
        foreach (var track in audio.Tracks(book.AudioStoredName))
        {
            var path = audio.TrackPath(book.AudioStoredName, track.Index);
            if (path is not null)
            {
                bytes += new FileInfo(path).Length;
            }
        }

        return new SyncFile(book.AudioFileName, bytes, hash);
    }

    private static bool MovePlace(Book book, SyncBook incoming)
    {
        if (incoming.EbookChapter is not int chapter || chapter < 0 || chapter <= (book.EbookChapter ?? -1))
        {
            return CopyHighlights(book, incoming.Highlights);
        }

        if (book.EbookChapter is null && chapter == 0)
        {
            book.EbookChapter = 0;
            return CopyHighlights(book, incoming.Highlights);
        }

        book.EbookChapter = chapter;
        CopyHighlights(book, incoming.Highlights);
        return true;
    }

    private static bool MoveListening(Book book, SyncBook incoming)
    {
        if (incoming.AudioTrack is not int track || incoming.AudioSeconds is not int seconds || track < 0 || seconds < 0)
        {
            return false;
        }

        if (!Further(track, seconds, book.AudioTrack, book.AudioSeconds))
        {
            return false;
        }

        book.AudioTrack = track;
        book.AudioSeconds = seconds;
        return true;
    }

    private static bool Further(int track, int seconds, int? currentTrack, int? currentSeconds)
    {
        var trackNow = currentTrack ?? 0;
        var secondsNow = currentSeconds ?? 0;
        if (currentTrack is null && currentSeconds is null && track == 0 && seconds == 0)
        {
            return false;
        }

        return track > trackNow || (track == trackNow && seconds > secondsNow);
    }

    private static bool CopyHighlights(Book book, IReadOnlyList<SyncHighlight>? incoming)
    {
        if (incoming is null || incoming.Count == 0)
        {
            return false;
        }

        var added = false;
        foreach (var mark in incoming.Take(500))
        {
            if (string.IsNullOrWhiteSpace(mark.Text))
            {
                continue;
            }

            var text = mark.Text.Trim();
            if (text.Length is 0 or > 1000 || mark.ChapterIndex < 0)
            {
                continue;
            }

            var note = string.IsNullOrWhiteSpace(mark.Note) ? null : mark.Note.Trim();
            if (note is { Length: > 2000 })
            {
                note = note[..2000];
            }

            var existing = book.Highlights.FirstOrDefault(item => item.ChapterIndex == mark.ChapterIndex && item.Text == text);
            if (existing is null)
            {
                book.Highlights.Add(new Highlight
                {
                    ChapterIndex = mark.ChapterIndex,
                    Text = text,
                    Note = note,
                    Prefix = Clip(mark.Prefix, keepEnd: true),
                    Suffix = Clip(mark.Suffix, keepEnd: false),
                    NotedAt = DateTimeOffset.UtcNow,
                });
                added = true;
            }
            else if (string.IsNullOrWhiteSpace(existing.Note) && note is not null)
            {
                existing.Note = note;
                added = true;
            }
        }

        return added;
    }

    private static string? Clip(string? value, bool keepEnd)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return value.Length <= 80 ? value : keepEnd ? value[^80..] : value[..80];
    }

    private static async Task<bool> TakeEbookAsync(
        EbookStore ebooks,
        Book book,
        SyncBook incoming,
        Func<SyncBook, string, CancellationToken, Task<Stream?>> download,
        CancellationToken cancellationToken)
    {
        var stream = await download(incoming, "ebook", cancellationToken);
        if (stream is null || incoming.Ebook is null)
        {
            return false;
        }

        await using (stream)
        {
            var saved = await ebooks.SaveAsync(stream, incoming.Ebook.FileName, cancellationToken);
            if (saved.Status != EbookSaveStatus.Saved || saved.StoredName is null || saved.FileName is null)
            {
                return false;
            }

            ebooks.Delete(book.EbookStoredName);
            book.EbookStoredName = saved.StoredName;
            book.EbookFileName = saved.FileName;
            book.EbookChapter = incoming.EbookChapter is > 0 ? incoming.EbookChapter : 0;
            book.Format ??= BookFormat.Ebook;
            Covers.Note(book, ebooks);
            return true;
        }
    }

    private static async Task<bool> TakeAudioAsync(
        AudioStore audio,
        Book book,
        SyncBook incoming,
        Func<SyncBook, string, CancellationToken, Task<Stream?>> download,
        CancellationToken cancellationToken)
    {
        var stream = await download(incoming, "audio", cancellationToken);
        if (stream is null || incoming.Audio is null)
        {
            return false;
        }

        await using (stream)
        {
            var length = stream.CanSeek ? stream.Length : incoming.Audio.Bytes;
            var name = incoming.Audio.FileName;
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var stem = Path.GetFileNameWithoutExtension(name);
                name = (string.IsNullOrWhiteSpace(stem) ? "audiobook" : stem) + ".zip";
            }

            var saved = await audio.SaveAsync([new AudioUpload(name, stream, length)], cancellationToken);
            if (saved.Status != AudioSaveStatus.Saved || saved.StoredName is null || saved.FileName is null)
            {
                return false;
            }

            audio.Delete(book.AudioStoredName);
            book.AudioStoredName = saved.StoredName;
            book.AudioFileName = saved.FileName;
            book.AudioTrack = incoming.AudioTrack is > 0 ? incoming.AudioTrack : 0;
            book.AudioSeconds = incoming.AudioSeconds is > 0 ? incoming.AudioSeconds : 0;
            book.Format ??= BookFormat.Audiobook;
            return true;
        }
    }

    private static async Task<bool> SendEbookAsync(
        EbookStore ebooks,
        Book book,
        string key,
        Func<string, string, Stream, string, CancellationToken, Task<int>> upload,
        CancellationToken cancellationToken)
    {
        var path = ebooks.OpenPath(book.EbookStoredName);
        if (path is null || book.EbookFileName is null)
        {
            return false;
        }

        await using var stream = File.OpenRead(path);
        var status = await upload(key, "ebook", stream, book.EbookFileName, cancellationToken);
        return status == StatusCodes.Status204NoContent;
    }

    private static async Task<bool> SendAudioAsync(
        AudioStore audio,
        Book book,
        string key,
        Func<string, string, Stream, string, CancellationToken, Task<int>> upload,
        CancellationToken cancellationToken)
    {
        if (book.AudioFileName is null)
        {
            return false;
        }

        var temp = Path.Combine(Path.GetTempPath(), $"shelf-sync-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var output = File.Create(temp))
            {
                await audio.WriteZipAsync(book.AudioStoredName, output, cancellationToken);
            }

            await using var stream = File.OpenRead(temp);
            var name = book.AudioFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? book.AudioFileName
                : Path.GetFileNameWithoutExtension(book.AudioFileName) + ".zip";
            var status = await upload(key, "audio", stream, name, cancellationToken);
            return status == StatusCodes.Status204NoContent;
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static async Task<Stream?> DownloadAsync(HttpClient client, SyncBook book, string kind, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync($"books/sync/{book.Key}/{kind}", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var temp = Path.Combine(Path.GetTempPath(), $"shelf-sync-{Guid.NewGuid():N}");
            await using (var file = File.Create(temp))
            await using (var body = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                await body.CopyToAsync(file, cancellationToken);
            }

            return new TempFileStream(temp);
        }
        catch (Exception ex) when (ex is HttpRequestException or Polly.ExecutionRejectedException or TaskCanceledException or IOException)
        {
            return null;
        }
    }

    private static async Task<string?> EnsureAsync(HttpClient client, SyncOffer offer, CancellationToken cancellationToken)
    {
        var response = await client.PostAsJsonAsync("books/sync/books", offer, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var place = await response.Content.ReadFromJsonAsync<SyncPlace>(cancellationToken);
        return place?.Key;
    }

    private static async Task<int> UploadAsync(
        HttpClient client,
        string key,
        string kind,
        Stream stream,
        string fileName,
        CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StreamContent(stream), "file", fileName);
        var response = await client.PostAsync($"books/sync/{key}/{kind}", content, cancellationToken);
        return (int)response.StatusCode;
    }

    private sealed class TempFileStream : FileStream
    {
        private readonly string _path;

        public TempFileStream(string path)
            : base(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.DeleteOnClose)
        {
            _path = path;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
    }
}

public sealed record SyncCatalog(SyncBook[] Books);

public sealed record SyncBook(
    string Key,
    string Title,
    string Author,
    string? Isbn,
    int? EbookChapter,
    int? AudioTrack,
    int? AudioSeconds,
    SyncFile? Ebook,
    SyncFile? Audio,
    SyncHighlight[] Highlights);

public sealed record SyncFile(string FileName, long Bytes, string Sha256);

public sealed record SyncHighlight(int ChapterIndex, string Text, string? Note, string? Prefix, string? Suffix);

public sealed record SyncProgress(
    string? EbookSha256,
    int? EbookChapter,
    string? AudioSha256,
    int? AudioTrack,
    int? AudioSeconds,
    SyncHighlight[]? Highlights);

public sealed record SyncOffer(string Title, string Author, string? Isbn);

public sealed record SyncPlace(string Key);
