using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

// One reading of one PDF file. It belongs to the file, not the reader: anyone who may open the
// file sees the same words, and a new upload starts a new reading.
public sealed class OcrScan
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public required string StoredName { get; set; }
    public int Pages { get; set; }
    public int Done { get; set; }
    public bool Failed { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// The words Tesseract found on a page that had no text of its own. Pages with their own text get no row.
public sealed class OcrPage
{
    public int Id { get; set; }
    public int ScanId { get; set; }
    public int Page { get; set; }
    public required string Words { get; set; }
}

public sealed record OcrPageResponse(string State, int Pages, int Done, OcrWord[]? Words);

// A word and its box, as fractions of the page's width and height, so it lines up at any size.
public sealed record OcrWord(string T, double X, double Y, double W, double H);

public sealed class OcrTools(IConfiguration configuration, ILogger<OcrTools> logger)
{
    private readonly Lazy<bool> _available = new(() => Probe(configuration, logger));

    public string Tesseract => configuration["Ocr:Tesseract"] ?? "tesseract";
    public string PdfToPpm => configuration["Ocr:PdfToPpm"] ?? "pdftoppm";
    public string PdfToText => configuration["Ocr:PdfToText"] ?? "pdftotext";
    public string PdfInfo => configuration["Ocr:PdfInfo"] ?? "pdfinfo";
    public string Languages => configuration["Ocr:Languages"] ?? "eng";
    public int Resolution => configuration.GetValue("Ocr:Resolution", 200);

    public bool Enabled => configuration.GetValue("Ocr:Enabled", true);

    // Tesseract and Poppler have to be installed on the server; without them scanned pages stay pictures.
    public bool Available => Enabled && _available.Value;

    private static bool Probe(IConfiguration configuration, ILogger logger)
    {
        foreach (var (key, name) in new[] { ("Ocr:Tesseract", "tesseract"), ("Ocr:PdfToPpm", "pdftoppm"), ("Ocr:PdfToText", "pdftotext"), ("Ocr:PdfInfo", "pdfinfo") })
        {
            var tool = configuration[key] ?? name;
            try
            {
                using var process = Process.Start(new ProcessStartInfo(tool, "-v")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                if (process is null || !process.WaitForExit(5000))
                {
                    logger.LogInformation("OCR is off: {Tool} did not answer.", tool);
                    return false;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                logger.LogInformation("OCR is off: {Tool} is not installed.", tool);
                return false;
            }
        }

        return true;
    }
}

public static class OcrRules
{
    // A page with fewer letters than this of its own is treated as a picture of text.
    public const int NativeTextLetters = 16;

    private static readonly JsonSerializerOptions WordJson = new(JsonSerializerDefaults.Web);

    public static string Serialize(IReadOnlyList<OcrWord> words) => JsonSerializer.Serialize(words, WordJson);

    public static OcrWord[] Deserialize(string words) => JsonSerializer.Deserialize<OcrWord[]>(words, WordJson) ?? [];

    public static int? ParsePageCount(string pdfInfo)
    {
        foreach (var line in pdfInfo.Split('\n'))
        {
            if (line.StartsWith("Pages:", StringComparison.Ordinal)
                && int.TryParse(line["Pages:".Length..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pages))
            {
                return pages;
            }
        }

        return null;
    }

    // Tesseract's TSV has one row per word at level 5, in reading order.
    public static OcrWord[] ParseTsv(string tsv, int imageWidth, int imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            return [];
        }

        var words = new List<OcrWord>();
        foreach (var line in tsv.Split('\n').Skip(1))
        {
            var cells = line.TrimEnd('\r').Split('\t');
            if (cells.Length < 12 || cells[0] != "5")
            {
                continue;
            }

            var text = cells[11].Trim();
            if (text.Length == 0
                || !int.TryParse(cells[6], CultureInfo.InvariantCulture, out var left)
                || !int.TryParse(cells[7], CultureInfo.InvariantCulture, out var top)
                || !int.TryParse(cells[8], CultureInfo.InvariantCulture, out var width)
                || !int.TryParse(cells[9], CultureInfo.InvariantCulture, out var height)
                || width <= 0 || height <= 0)
            {
                continue;
            }

            words.Add(new OcrWord(
                text,
                Math.Round((double)left / imageWidth, 5),
                Math.Round((double)top / imageHeight, 5),
                Math.Round((double)width / imageWidth, 5),
                Math.Round((double)height / imageHeight, 5)));
        }

        return words.ToArray();
    }

    // Width and height from a PNG's header, which is all the page image is needed for afterwards.
    public static (int Width, int Height) PngSize(string path)
    {
        Span<byte> header = stackalloc byte[24];
        using var file = File.OpenRead(path);
        if (file.Read(header) < 24)
        {
            return (0, 0);
        }

        static int BigEndian(ReadOnlySpan<byte> bytes) => (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
        return (BigEndian(header[16..20]), BigEndian(header[20..24]));
    }

    public static async Task<OcrPageResponse> PageAsync(
        ShelfDb db,
        OcrTools tools,
        OcrService service,
        OpenBook open,
        int page,
        CancellationToken cancellationToken)
    {
        var storedName = open.Book.EbookStoredName;
        if (!EbookStore.IsPdf(storedName))
        {
            return new OcrPageResponse("none", 0, 0, null);
        }

        if (!tools.Available)
        {
            return new OcrPageResponse("unavailable", 0, 0, null);
        }

        var scan = await db.OcrScans.AsNoTracking()
            .FirstOrDefaultAsync(item => item.BookId == open.Book.Id && item.StoredName == storedName, cancellationToken);
        if (scan is null)
        {
            service.Nudge();
            return new OcrPageResponse("queued", 0, 0, null);
        }

        var words = await db.OcrPages.AsNoTracking()
            .Where(item => item.ScanId == scan.Id && item.Page == page)
            .Select(item => item.Words)
            .FirstOrDefaultAsync(cancellationToken);
        var state = scan.Failed ? "failed" : scan.Done >= scan.Pages ? "done" : "reading";
        if (state == "reading")
        {
            service.Nudge();
        }

        return new OcrPageResponse(state, scan.Pages, scan.Done, words is null ? null : Deserialize(words));
    }

    public static async Task<Results<Ok<OcrPageResponse>, NotFound>> Page(
        int id,
        int page,
        ShelfDb db,
        OcrTools tools,
        OcrService service,
        CancellationToken cancellationToken)
    {
        if (page < 0 || await Lending.OpenAsync(db, id, cancellationToken) is not { } open)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(await PageAsync(db, tools, service, open, page, cancellationToken));
    }
}

// Reads every e-book in the background, one at a time, so its words are ready before anyone asks:
// each EPUB chapter and PDF page is kept for searching, and scanned PDF pages are read with OCR.
public sealed class OcrService(
    IServiceScopeFactory scopes,
    OcrTools tools,
    EbookStore ebooks,
    ILogger<OcrService> logger) : BackgroundService
{
    private static readonly TimeSpan Sweep = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(2);

    private readonly Channel<bool> _nudges = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
    });

    // Something changed (an upload, or a reader opening a PDF): look for work now instead of at the next sweep.
    public void Nudge() => _nudges.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (await NextAsync(stoppingToken) is { } work)
                {
                    await ReadAsync(work.BookId, work.StoredName, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Reading scanned pages stopped; it will try again.");
            }

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            wait.CancelAfter(Sweep);
            try
            {
                await _nudges.Reader.ReadAsync(wait.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
            }
        }
    }

    // The next PDF whose current file has not been read through, after clearing readings of files that are gone.
    private async Task<(int BookId, string StoredName)?> NextAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var stale = await db.OcrScans
            .Where(scan => !db.Books.IgnoreQueryFilters().Any(book => book.Id == scan.BookId && book.EbookStoredName == scan.StoredName))
            .ToListAsync(cancellationToken);
        if (stale.Count > 0)
        {
            var ids = stale.Select(scan => scan.Id).ToList();
            await db.OcrPages.Where(page => ids.Contains(page.ScanId)).ExecuteDeleteAsync(cancellationToken);
            await db.BookTexts.Where(text => ids.Contains(text.ScanId)).ExecuteDeleteAsync(cancellationToken);
            db.OcrScans.RemoveRange(stale);
            await db.SaveChangesAsync(cancellationToken);
        }

        var pdfs = await db.Books.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(book => book.EbookStoredName != null && (book.EbookStoredName.EndsWith(".pdf") || book.EbookStoredName.EndsWith(".epub")))
            .Select(book => new { book.Id, StoredName = book.EbookStoredName! })
            .ToListAsync(cancellationToken);

        // An EPUB needs nothing installed; a PDF needs Poppler, and Tesseract for its scanned pages.
        foreach (var pdf in pdfs.Where(item => tools.Available || EbookStore.IsEpub(item.StoredName)))
        {
            var scan = await db.OcrScans.AsNoTracking()
                .FirstOrDefaultAsync(item => item.BookId == pdf.Id && item.StoredName == pdf.StoredName, cancellationToken);
            if (scan is null || (!scan.Failed && scan.Done < scan.Pages))
            {
                return (pdf.Id, pdf.StoredName);
            }
        }

        return null;
    }

    private async Task ReadAsync(int bookId, string storedName, CancellationToken cancellationToken)
    {
        var path = ebooks.OpenPath(storedName);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var scan = await db.OcrScans.FirstOrDefaultAsync(item => item.BookId == bookId && item.StoredName == storedName, cancellationToken);
        if (scan is null)
        {
            scan = new OcrScan { BookId = bookId, StoredName = storedName, UpdatedAt = DateTimeOffset.UtcNow };
            db.OcrScans.Add(scan);
        }

        if (EbookStore.IsEpub(storedName))
        {
            await ReadEpubAsync(db, scan, path, cancellationToken);
            return;
        }

        var info = path is null ? null : await RunAsync(tools.PdfInfo, [path], cancellationToken);
        if (info is null || OcrRules.ParsePageCount(info) is not int pages || pages <= 0)
        {
            scan.Failed = true;
            scan.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Could not read the pages of {StoredName}.", storedName);
            return;
        }

        scan.Pages = pages;
        await db.SaveChangesAsync(cancellationToken);
        var folder = Path.Combine(Path.GetTempPath(), $"shelf-ocr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            for (var page = scan.Done; page < pages; page++)
            {
                var number = (page + 1).ToString(CultureInfo.InvariantCulture);
                var own = await RunAsync(tools.PdfToText, ["-f", number, "-l", number, path!, "-"], cancellationToken) ?? "";
                if (own.Count(char.IsLetterOrDigit) < OcrRules.NativeTextLetters)
                {
                    var words = await RecognizeAsync(path!, number, folder, cancellationToken);
                    if (words.Length > 0)
                    {
                        db.OcrPages.Add(new OcrPage { ScanId = scan.Id, Page = page, Words = OcrRules.Serialize(words) });
                        Keep(db, scan, page, string.Join(' ', words.Select(word => word.T)));
                    }
                }
                else
                {
                    Keep(db, scan, page, own);
                }

                // Saved a page at a time, so a restart picks up where it stopped and readers see pages as they come.
                scan.Done = page + 1;
                scan.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void Keep(ShelfDb db, OcrScan scan, int part, string text)
    {
        var plain = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        if (plain.Length > 0)
        {
            db.BookTexts.Add(new BookText { ScanId = scan.Id, BookId = scan.BookId, Part = part, Text = plain });
        }
    }

    // An EPUB's chapters, kept as plain text for searching.
    private async Task ReadEpubAsync(ShelfDb db, OcrScan scan, string? path, CancellationToken cancellationToken)
    {
        var chapters = path is null ? null : EpubFile.Chapters(path);
        if (chapters is null)
        {
            scan.Failed = true;
            scan.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        scan.Pages = chapters.Count;
        await db.SaveChangesAsync(cancellationToken);
        for (var index = scan.Done; index < chapters.Count; index++)
        {
            if (EpubFile.ChapterHtml(path!, index, "") is { } html)
            {
                Keep(db, scan, index, Search.PlainText(html));
            }

            scan.Done = index + 1;
            scan.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<OcrWord[]> RecognizeAsync(string path, string number, string folder, CancellationToken cancellationToken)
    {
        var image = Path.Combine(folder, "page");
        var resolution = tools.Resolution.ToString(CultureInfo.InvariantCulture);
        if (await RunAsync(tools.PdfToPpm, ["-f", number, "-l", number, "-r", resolution, "-gray", "-png", "-singlefile", path, image], cancellationToken) is null)
        {
            return [];
        }

        var png = image + ".png";
        if (!File.Exists(png))
        {
            return [];
        }

        try
        {
            var tsv = await RunAsync(tools.Tesseract, [png, "stdout", "-l", tools.Languages, "tsv"], cancellationToken);
            var (width, height) = OcrRules.PngSize(png);
            return tsv is null ? [] : OcrRules.ParseTsv(tsv, width, height);
        }
        finally
        {
            File.Delete(png);
        }
    }

    private async Task<string?> RunAsync(string tool, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ToolTimeout);
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var text = await output;
            await errors;
            return process.ExitCode == 0 ? text : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            logger.LogInformation("{Tool} took too long and was stopped.", tool);
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
