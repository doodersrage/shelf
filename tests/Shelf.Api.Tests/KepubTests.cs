using System.IO.Compression;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class KepubTests
{
    private static readonly string Fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "e2e", "fixtures"));

    [Fact]
    public void Each_sentence_goes_in_a_numbered_span_and_the_body_in_kobos_wrappers()
    {
        var chapter = KepubConverter.Chapter("""
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE html>
            <html xmlns="http://www.w3.org/1999/xhtml"><head><title>One</title></head>
            <body><h1>Loomings</h1><p>Call me Ishmael. Some years ago&nbsp;— never mind how long! I thought I would sail.</p><p>A <em>second</em> one.</p><script>no()</script></body></html>
            """);
        Assert.Contains("<div id=\"book-columns\"><div id=\"book-inner\"><h1><span class=\"koboSpan\" id=\"kobo.1.1\">Loomings</span></h1>", chapter, StringComparison.Ordinal);
        Assert.Contains("<span class=\"koboSpan\" id=\"kobo.2.1\">Call me Ishmael.</span> <span class=\"koboSpan\" id=\"kobo.2.2\">Some years ago — never mind how long!</span> <span class=\"koboSpan\" id=\"kobo.2.3\">I thought I would sail.</span>", chapter, StringComparison.Ordinal);
        Assert.Contains("<em><span class=\"koboSpan\" id=\"kobo.3.2\">second</span></em>", chapter, StringComparison.Ordinal);
        Assert.Contains("<script>no()</script>", chapter, StringComparison.Ordinal);
        Assert.Contains("kobostylehacks", chapter, StringComparison.Ordinal);
        Assert.Contains("encoding=\"utf-8\"", chapter, StringComparison.Ordinal);
        // Twice is the same as once, and what is not XML is left alone.
        Assert.Equal(chapter, KepubConverter.Chapter(chapter));
        Assert.Equal("<p>not <b>closed</p>", KepubConverter.Chapter("<p>not <b>closed</p>"));
    }

    [Fact]
    public void A_whole_epub_becomes_a_kepub_with_its_mimetype_first()
    {
        var output = Path.Combine(Path.GetTempPath(), $"shelf-{Guid.NewGuid():N}.kepub.epub");
        try
        {
            Assert.True(KepubConverter.Convert(Path.Combine(Fixtures, "moby.epub"), output));
            using var zip = ZipFile.OpenRead(output);
            Assert.Equal("mimetype", zip.Entries[0].FullName);
            Assert.Equal(zip.Entries[0].Length, zip.Entries[0].CompressedLength);
            using var reader = new StreamReader(zip.GetEntry("OEBPS/c1.xhtml")!.Open());
            var text = reader.ReadToEnd();
            Assert.Contains("koboSpan", text, StringComparison.Ordinal);
            Assert.NotNull(EpubFile.Chapters(output));
        }
        finally
        {
            File.Delete(output);
        }
    }
}
