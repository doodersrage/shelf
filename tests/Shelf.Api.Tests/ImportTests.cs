using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shelf.Api.Books;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

public sealed class ImportTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // A one-pixel PNG, for a cover.
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    [Fact]
    public async Task E_books_become_books_named_from_what_they_say_with_their_own_covers()
    {
        var client = factory.Client;
        var results = await ImportAsync(client, false,
            ("left-hand.epub", Epub("The Left Hand of Darkness", "Ursula K. Le Guin", "978-0-441-47812-5", "en", cover: true)),
            ("dispossessed.epub", Epub("The Dispossessed", "Ursula K. Le Guin", null, "en-GB", cover: false)),
            ("notes.txt", "not a book"u8.ToArray()));

        Assert.Equal([ImportOutcome.Added, ImportOutcome.Added, ImportOutcome.Unsupported], results.Select(result => result.Outcome));
        var left = await client.GetFromJsonAsync<BookResponse>($"/books/{results[0].BookId}", JsonOptions);
        Assert.Equal("The Left Hand of Darkness", left!.Title);
        Assert.Equal("Ursula K. Le Guin", left.Author);
        Assert.Equal("9780441478125", left.Isbn);
        Assert.Equal("English", left.Language);
        Assert.Equal(BookFormat.Ebook, left.Format);
        Assert.Equal("left-hand.epub", left.EbookFileName);

        // The cover inside the file is served and shown; a book without one keeps its plain spine.
        var cover = await client.GetAsync($"/books/{left.Id}/cover");
        Assert.Equal("image/png", cover.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await cover.Content.ReadAsByteArrayAsync());
        Assert.Contains($"src=\"/books/{left.Id}/cover\"", await client.GetStringAsync($"/library/{left.Id}"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/books/{results[1].BookId}/cover")).StatusCode);

        // Another reader sees it only once the shelf is open.
        var stranger = await factory.SignUpAsync("Cover Stranger");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/books/{left.Id}/cover")).StatusCode);
        await client.PutAsJsonAsync("/books/shelves/open", new ShelfOpenChange(true), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, (await stranger.GetAsync($"/books/{left.Id}/cover")).StatusCode);
        await client.PutAsJsonAsync("/books/shelves/open", new ShelfOpenChange(false), JsonOptions);
    }

    [Fact]
    public async Task A_file_already_on_the_shelf_is_skipped_and_a_matching_book_takes_the_file()
    {
        var client = factory.Client;
        var epub = Epub("The Word for World Is Forest", "Ursula K. Le Guin", null, "en", cover: false);
        var first = Assert.Single(await ImportAsync(client, false, ("forest.epub", epub)));
        Assert.Equal(ImportOutcome.Added, first.Outcome);

        var again = Assert.Single(await ImportAsync(client, false, ("forest-again.epub", epub)));
        Assert.Equal(ImportOutcome.AlreadyOnShelf, again.Outcome);
        Assert.Equal(first.BookId, again.SameAsId);
        Assert.Equal(ImportOutcome.Added, Assert.Single(await ImportAsync(client, true, ("forest-again.epub", epub))).Outcome);

        // A book typed in by hand, with no e-book yet, takes the file rather than a second book being made.
        var typed = await (await client.PostAsJsonAsync("/books", new CreateBookRequest("The Lathe of Heaven", "Ursula K. Le Guin", BookStatus.Want, null), JsonOptions))
            .Content.ReadFromJsonAsync<BookResponse>(JsonOptions);
        var joined = Assert.Single(await ImportAsync(client, false, ("lathe.epub", Epub("The Lathe of Heaven", "Ursula K. Le Guin", null, "en", cover: false))));
        Assert.Equal(ImportOutcome.AddedToExisting, joined.Outcome);
        Assert.Equal(typed!.Id, joined.BookId);
    }

    [Fact]
    public async Task Tracks_sharing_an_album_make_one_audiobook_and_a_form_lands_on_what_it_made()
    {
        var client = factory.Client;
        var results = await ImportAsync(client, false,
            ("01.mp3", Mp3("Chapter One", "A Wizard of Earthsea", "Ursula K. Le Guin")),
            ("02.mp3", Mp3("Chapter Two", "A Wizard of Earthsea", "Ursula K. Le Guin")),
            ("loose.mp3", Mp3("A Single Story", null, "Someone Else")));

        Assert.Equal(2, results.Count);
        var earthsea = await client.GetFromJsonAsync<BookResponse>($"/books/{results.Single(result => result.Title == "A Wizard of Earthsea").BookId}", JsonOptions);
        Assert.Equal("Ursula K. Le Guin", earthsea!.Author);
        Assert.Equal("2 tracks", earthsea.AudioFileName);
        Assert.Equal(BookFormat.Audiobook, earthsea.Format);
        Assert.Contains(results, result => result.Title == "A Single Story");

        // Without tags, the file name is the title and the author is left to fill in.
        using var content = new MultipartFormDataContent { { new ByteArrayContent(Epub(null, null, null, null, cover: false)), "file", "the_tombs_of_atuan.epub" } };
        var response = await client.PostAsync("/books/import", content);
        Assert.Matches(@"^/library/\d+$", response.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Equal("?imported=1", response.RequestMessage.RequestUri.Query);
        var page = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("the tombs of atuan", page);
        Assert.Contains(BookImport.UnknownAuthor, page);
        Assert.Contains("Made from the file.", page);
    }

    [Fact]
    public void Tags_are_read_from_mp3_and_epub_details_from_the_package()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"shelf-tags-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var mp3 = Path.Combine(folder, "a.mp3");
            File.WriteAllBytes(mp3, Mp3("Track", "The Album", "The Reader"));
            var tags = AudioDetails.Read(mp3)!;
            Assert.Equal(("The Album", "The Reader", "The Album"), (tags.Title, tags.Author, tags.Album));

            var epub = Path.Combine(folder, "a.epub");
            File.WriteAllBytes(epub, Epub("Title Here", "Author Here", "0-441-47812-3", "fr", cover: true, year: "1969-03-01"));
            var details = EpubFile.Details(epub)!;
            Assert.Equal(("Title Here", "Author Here", "0441478123", 1969, "fr"), (details.Title, details.Author, details.Isbn, details.Year, details.Language));
            Assert.Equal("image/png", EpubFile.Cover(epub)?.ContentType);
            Assert.Equal("the left hand", BookImport.TitleFromName("the_left_hand.epub"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Notes_come_out_as_markdown_and_a_borrower_gets_only_their_own()
    {
        var client = factory.Client;
        var book = await (await client.PostAsJsonAsync("/books", new CreateBookRequest("A \"Quoted\" Title", "Someone", BookStatus.Finished, 4,
                Notes: "Read on the ferry.", Review: "Better the second time."), JsonOptions))
            .Content.ReadFromJsonAsync<BookResponse>(JsonOptions);
        await client.PostAsJsonAsync($"/books/{book!.Id}/quotes", new CreateQuoteRequest("Two lines,\nkept together.", 12), JsonOptions);
        await client.PostAsJsonAsync($"/books/{book.Id}/highlights", new CreateHighlightRequest("A marked passage", 2, "Why it matters."), JsonOptions);

        var response = await client.GetAsync($"/books/{book.Id}/notes.md");
        Assert.Equal("text/markdown", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Someone - A Quoted Title.md", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var markdown = await response.Content.ReadAsStringAsync();
        Assert.Contains("title: \"A \\\"Quoted\\\" Title\"", markdown);
        Assert.Contains("rating: 4", markdown);
        Assert.Contains("## Review\n\nBetter the second time.", markdown);
        Assert.Contains("## Notes\n\nRead on the ferry.", markdown);
        Assert.Contains("> Two lines,\n> kept together.\n>\n> — page 12", markdown);
        Assert.Contains("### Chapter 3\n\n> A marked passage\n\nWhy it matters.", markdown);

        var borrower = await factory.SignUpAsync("Notes Borrower");
        await client.PostAsJsonAsync($"/books/{book.Id}/lend", new LendRequest(await factory.ReaderIdAsync("Notes Borrower")), JsonOptions);
        await borrower.PostAsJsonAsync($"/books/{book.Id}/highlights", new CreateHighlightRequest("The borrower's passage", 0, "Mine."), JsonOptions);
        var theirs = await borrower.GetStringAsync($"/books/{book.Id}/notes.md");
        Assert.Contains("The borrower's passage", theirs);
        Assert.DoesNotContain("A marked passage", theirs);
        Assert.DoesNotContain("ferry", theirs);
        Assert.DoesNotContain("rating", theirs);

        using var zip = new ZipArchive(await client.GetStreamAsync("/books/notes.zip"));
        Assert.Contains(zip.Entries, entry => entry.Name == "Someone - A Quoted Title.md");
        using var borrowed = new ZipArchive(await borrower.GetStreamAsync("/books/notes.zip"));
        Assert.Equal(["Someone - A Quoted Title.md"], borrowed.Entries.Select(entry => entry.Name));
        await client.PostAsync($"/books/{book.Id}/return", null);
    }

    private static async Task<List<ImportedFile>> ImportAsync(HttpClient client, bool keepBoth, params (string Name, byte[] Bytes)[] files)
    {
        using var content = new MultipartFormDataContent();
        foreach (var (name, bytes) in files)
        {
            content.Add(new ByteArrayContent(bytes), "file", name);
        }

        if (keepBoth)
        {
            content.Add(new StringContent("true"), "keepBoth");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/books/import") { Content = content };
        request.Headers.Accept.ParseAdd("application/json");
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<ImportedFile>>(JsonOptions))!;
    }

    internal static byte[] Epub(string? title, string? author, string? isbn, string? language, bool cover, string? year = null)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "META-INF/container.xml", """
                <?xml version="1.0"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
                </container>
                """);
            var metadata = new StringBuilder();
            if (title is not null) metadata.Append($"<dc:title>{title}</dc:title>");
            if (author is not null) metadata.Append($"<dc:creator opf:role=\"aut\">{author}</dc:creator>");
            if (isbn is not null) metadata.Append($"<dc:identifier>urn:isbn:{isbn}</dc:identifier>");
            if (language is not null) metadata.Append($"<dc:language>{language}</dc:language>");
            if (year is not null) metadata.Append($"<dc:date>{year}</dc:date>");
            Write(zip, "OEBPS/content.opf", $"""
                <?xml version="1.0"?>
                <package xmlns="http://www.idpf.org/2007/opf" xmlns:opf="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">{metadata}<dc:identifier id="id">{Guid.NewGuid()}</dc:identifier></metadata>
                  <manifest>
                    <item id="c1" href="chapter1.xhtml" media-type="application/xhtml+xml"/>
                    {(cover ? "<item id=\"cover\" href=\"cover.png\" media-type=\"image/png\" properties=\"cover-image\"/>" : "")}
                  </manifest>
                  <spine><itemref idref="c1"/></spine>
                </package>
                """);
            Write(zip, "OEBPS/chapter1.xhtml", $"""
                <?xml version="1.0"?>
                <html xmlns="http://www.w3.org/1999/xhtml"><head><title>One</title></head><body><p>{title} {Guid.NewGuid()}</p></body></html>
                """);
            if (cover)
            {
                using var stream = zip.CreateEntry("OEBPS/cover.png").Open();
                stream.Write(Png);
            }
        }

        return memory.ToArray();

        static void Write(ZipArchive zip, string name, string text)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(text);
        }
    }

    // An ID3v2.3 tag with title, album, and artist frames, then a little stand-in audio.
    internal static byte[] Mp3(string title, string? album, string artist)
    {
        var frames = new MemoryStream();
        void Frame(string id, string text)
        {
            var body = new byte[] { 3 }.Concat(Encoding.UTF8.GetBytes(text)).ToArray();
            frames.Write(Encoding.ASCII.GetBytes(id));
            frames.Write([(byte)(body.Length >> 24), (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length]);
            frames.Write([0, 0]);
            frames.Write(body);
        }

        Frame("TIT2", title);
        if (album is not null)
        {
            Frame("TALB", album);
        }

        Frame("TPE1", artist);
        var size = (int)frames.Length;
        var output = new MemoryStream();
        output.Write("ID3"u8);
        output.Write([3, 0, 0, (byte)((size >> 21) & 0x7f), (byte)((size >> 14) & 0x7f), (byte)((size >> 7) & 0x7f), (byte)(size & 0x7f)]);
        output.Write(frames.ToArray());
        output.Write(Encoding.ASCII.GetBytes($"audio for {title} {Guid.NewGuid()}"));
        return output.ToArray();
    }
}
