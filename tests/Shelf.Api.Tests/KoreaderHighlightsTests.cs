using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class KoreaderHighlightsTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static readonly string[] Sentences =
    [
        "Call me Ishmael, and mind the weather.",
        "The sea was calm and the ship was slow.",
        "A white whale rose out of the morning mist.",
    ];

    [Fact]
    public async Task Highlights_exported_from_koreader_land_in_their_chapters_with_their_notes()
    {
        var client = await factory.SignUpAsync("KOReader Highlighter");
        var book = await (await client.PostAsJsonAsync("/books", new CreateBookRequest("A Sea Story", "Herman Example", BookStatus.Reading, null), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions);
        using (var content = new MultipartFormDataContent { { new ByteArrayContent(Epub()), "file", "sea-story.epub" } })
        {
            Assert.True((await client.PostAsync($"/books/{book!.Id}/ebook", content)).IsSuccessStatusCode);
        }

        // As KOReader's JSON exporter writes it for several books at once.
        var export = $$"""
            {
              "created_on": 1760000000,
              "version": "v2025.08",
              "documents": [
                {
                  "title": "A Sea Story",
                  "author": "Herman Example",
                  "file": "/mnt/onboard/Books/sea-story.epub",
                  "number_of_pages": 120,
                  "entries": [
                    { "sort": "highlight", "page": 40, "time": 1759990000, "text": "A white whale rose", "chapter": "Three", "pn_xp": "/body/DocFragment[3]/body/p/text().0" },
                    { "sort": "highlight", "page": 12, "time": 1759991000, "text": "the ship was slow", "note": "Too slow.", "chapter": "Two", "pn_xp": "/body/DocFragment[1]/body/p/text().0" },
                    { "sort": "highlight", "page": 2, "time": 1759992000, "text": "Call me Ishmael" },
                    { "sort": "bookmark", "page": 3, "time": 1759993000, "text": "Page 3" }
                  ]
                },
                { "title": "Not On This Shelf", "author": "Nobody", "entries": [ { "sort": "highlight", "text": "Anything", "page": 1 } ] }
              ]
            }
            """;
        var result = await UploadAsync(client, export);
        Assert.Equal((3, 0, 1), (result.Added, result.AlreadyHere, result.Books));
        Assert.Equal(["Not On This Shelf"], result.NotFound);

        var marks = await client.GetFromJsonAsync<List<HighlightResponse>>($"/books/{book.Id}/highlights", JsonOptions);
        var whale = marks!.Single(mark => mark.Text == "A white whale rose");
        Assert.Equal(2, whale.ChapterIndex);
        // KOReader's position said the first chapter; the words are in the second, so that is where it goes.
        var ship = marks.Single(mark => mark.Text == "the ship was slow");
        Assert.Equal((1, "Too slow."), (ship.ChapterIndex, ship.Note));
        Assert.Equal(0, marks.Single(mark => mark.Text == "Call me Ishmael").ChapterIndex);

        // A single-book export of the same highlights adds nothing new.
        var again = await UploadAsync(client, """
            { "title": "a sea story", "author": "Herman Example", "entries": [ { "sort": "highlight", "text": "A white whale rose", "pn_xp": "/body/DocFragment[3]/body/p/text().0" } ], "created_on": 1760000001 }
            """);
        Assert.Equal((0, 1), (again.Added, again.AlreadyHere));

        var bad = await client.PostAsync("/books/highlights/koreader", new MultipartFormDataContent { { new ByteArrayContent("not json"u8.ToArray()), "file", "notes.json" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    private static async Task<KoreaderImport> UploadAsync(HttpClient client, string json)
    {
        using var content = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(json)), "file", "highlights.json" } };
        var response = await client.PostAsync("/books/highlights/koreader", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<KoreaderImport>(JsonOptions))!;
    }

    private static byte[] Epub()
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Write(string name, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(text);
            }

            Write("META-INF/container.xml", """<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>""");
            var items = string.Concat(Enumerable.Range(1, 3).Select(n => $"<item id=\"c{n}\" href=\"c{n}.xhtml\" media-type=\"application/xhtml+xml\"/>"));
            var spine = string.Concat(Enumerable.Range(1, 3).Select(n => $"<itemref idref=\"c{n}\"/>"));
            Write("content.opf", $"""<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>A Sea Story</dc:title></metadata><manifest>{items}</manifest><spine>{spine}</spine></package>""");
            for (var n = 1; n <= 3; n++)
            {
                Write($"c{n}.xhtml", $"""<?xml version="1.0"?><html xmlns="http://www.w3.org/1999/xhtml"><head><title>Chapter {n}</title></head><body><p>Before. {Sentences[n - 1]} After.</p></body></html>""");
            }
        }

        return memory.ToArray();
    }
}
