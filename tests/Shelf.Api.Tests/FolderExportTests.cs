using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class FolderExportTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    // Each book a folder under its author, as Calibre lays them out, with its formats named for it and an OPF that
    // says what it is in Calibre's terms. Only the reader's own books, and none of their notes.
    [Fact]
    public async Task The_books_go_out_as_author_and_title_folders_with_an_opf_each()
    {
        var reader = await factory.SignUpAsync("Folder Exporter");
        var book = (await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("A Wizard: of Earthsea", "Ursula K. Le Guin", BookStatus.Finished, 4,
            Year: 1968, Isbn: "9780547773742", Language: "English", Series: "Earthsea", SeriesNumber: 1, Tags: ["fantasy"], Notes: "A private note."), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions))!;
        using (var content = new MultipartFormDataContent { { new ByteArrayContent(BooksEndpointTests.SampleEpub("Ged's story.")), "file", "wizard.epub" } })
        {
            await reader.PostAsync($"/books/{book.Id}/ebook", content);
        }

        using (var content = new MultipartFormDataContent { { new ByteArrayContent(System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF\n")), "file", "wizard.pdf" } })
        {
            await reader.PostAsync($"/books/{book.Id}/ebook/formats", content);
        }

        var stranger = await factory.SignUpAsync("Folder Stranger");
        await stranger.PostAsJsonAsync("/books", new CreateBookRequest("Not Yours", "Someone Else", BookStatus.Want, null), JsonOptions);

        using var zip = new ZipArchive(new MemoryStream(await reader.GetByteArrayAsync("/books/export/folders")));
        var folder = $"Ursula K. Le Guin/A Wizard_ of Earthsea ({book.Id})/";
        var names = zip.Entries.Select(entry => entry.FullName).ToList();
        Assert.Contains(folder + "metadata.opf", names);
        Assert.Contains(folder + "A Wizard_ of Earthsea - Ursula K. Le Guin.epub", names);
        Assert.Contains(folder + "A Wizard_ of Earthsea - Ursula K. Le Guin.pdf", names);
        Assert.DoesNotContain(names, name => name.Contains("Not Yours", StringComparison.Ordinal));

        using var opf = zip.GetEntry(folder + "metadata.opf")!.Open();
        var package = XDocument.Load(opf);
        var metadata = package.Root!.Element(Opf + "metadata")!;
        Assert.Equal("A Wizard: of Earthsea", metadata.Element(Dc + "title")!.Value);
        Assert.Equal("Ursula K. Le Guin", metadata.Element(Dc + "creator")!.Value);
        Assert.Equal("en", metadata.Element(Dc + "language")!.Value);
        Assert.Equal("9780547773742", metadata.Elements(Dc + "identifier").Single(item => (string?)item.Attribute(Opf + "scheme") == "ISBN").Value);
        Assert.Equal("fantasy", metadata.Element(Dc + "subject")!.Value);
        string? Meta(string name) => metadata.Elements(Opf + "meta").FirstOrDefault(meta => (string?)meta.Attribute("name") == name)?.Attribute("content")?.Value;
        Assert.Equal(("Earthsea", "1", "8"), (Meta("calibre:series"), Meta("calibre:series_index"), Meta("calibre:rating")));
        Assert.DoesNotContain("private note", package.ToString());
    }
}
