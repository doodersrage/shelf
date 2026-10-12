using System.IO.Compression;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class EpubContentsTests
{
    private const string Doctype = """<!DOCTYPE html PUBLIC "-//W3C//DTD XHTML 1.1//EN" "http://www.w3.org/TR/xhtml11/DTD/xhtml11.dtd">""";

    private static string Chapter(string title, string heading) =>
        $"""<?xml version="1.0" encoding="utf-8"?>{Doctype}<html xmlns="http://www.w3.org/1999/xhtml"><head><title>{title}</title></head><body>{heading}<p>Words&nbsp;here.</p></body></html>""";

    private static string Write(Dictionary<string, string> files, string manifest, string spine)
    {
        var path = Path.Combine(Path.GetTempPath(), $"contents-{Guid.NewGuid():N}.epub");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Put(string name, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(text);
            }

            Put("META-INF/container.xml", """<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>""");
            Put("OEBPS/content.opf", $"""<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>The Book</dc:title></metadata><manifest>{manifest}</manifest>{spine}</package>""");
            foreach (var (name, text) in files)
            {
                Put(name, text);
            }
        }

        return path;
    }

    [Fact]
    public void Chapters_take_their_names_from_the_books_own_table_of_contents()
    {
        // As Calibre makes them: every file titled with the book's title, and a doctype on each.
        var path = Write(new()
        {
            ["OEBPS/Text/one.xhtml"] = Chapter("The Book", ""),
            ["OEBPS/Text/two.xhtml"] = Chapter("The Book", ""),
            ["OEBPS/nav.xhtml"] = $"""<?xml version="1.0"?>{Doctype}<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><body><nav epub:type="landmarks"><ol><li><a href="Text/two.xhtml">Wrong list</a></li></ol></nav><nav epub:type="toc"><ol><li><a href="Text/one.xhtml">I.  The&nbsp;Arrival</a></li><li><a href="Text/two.xhtml#start">II. The Storm</a><ol><li><a href="Text/two.xhtml#later">Later on</a></li></ol></li></ol></nav></body></html>""",
        }, """<item id="nav" href="nav.xhtml" properties="nav" media-type="application/xhtml+xml"/><item id="a" href="Text/one.xhtml" media-type="application/xhtml+xml"/><item id="b" href="Text/two.xhtml" media-type="application/xhtml+xml"/>""",
        """<spine><itemref idref="a"/><itemref idref="b"/></spine>""");
        try
        {
            Assert.Equal(["I. The Arrival", "II. The Storm"], EpubFile.Chapters(path)!.Select(chapter => chapter.Title));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_older_books_NCX_names_its_chapters_and_a_book_without_one_uses_each_chapters_heading()
    {
        var ncx = Write(new()
        {
            ["OEBPS/one.xhtml"] = Chapter("The Book", ""),
            ["OEBPS/toc.ncx"] = """<?xml version="1.0"?><!DOCTYPE ncx PUBLIC "-//NISO//DTD ncx 2005-1//EN" "http://www.daisy.org/z3986/2005/ncx-2005-1.dtd"><ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1"><navMap><navPoint id="p1"><navLabel><text>Chapter One</text></navLabel><content src="one.xhtml"/></navPoint></navMap></ncx>""",
        }, """<item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/><item id="a" href="one.xhtml" media-type="application/xhtml+xml"/>""",
        """<spine toc="ncx"><itemref idref="a"/></spine>""");
        var plain = Write(new()
        {
            ["OEBPS/one.xhtml"] = Chapter("The Book", "<h1>A Heading</h1>"),
            ["OEBPS/two.xhtml"] = Chapter("Its Title", ""),
            ["OEBPS/three.xhtml"] = "not XML at all <",
        }, """<item id="a" href="one.xhtml" media-type="application/xhtml+xml"/><item id="b" href="two.xhtml" media-type="application/xhtml+xml"/><item id="c" href="three.xhtml" media-type="application/xhtml+xml"/>""",
        """<spine><itemref idref="a"/><itemref idref="b"/><itemref idref="c"/></spine>""");
        try
        {
            Assert.Equal(["Chapter One"], EpubFile.Chapters(ncx)!.Select(chapter => chapter.Title));
            // Its heading, else its title, else its file.
            Assert.Equal(["A Heading", "Its Title", "three"], EpubFile.Chapters(plain)!.Select(chapter => chapter.Title));
        }
        finally
        {
            File.Delete(ncx);
            File.Delete(plain);
        }
    }
}
