using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class OcrTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void Tesseract_words_become_boxes_on_the_page()
    {
        const string tsv = """
            level	page_num	block_num	par_num	line_num	word_num	left	top	width	height	conf	text
            1	1	0	0	0	0	0	0	1000	2000	-1
            4	1	1	1	1	0	100	200	600	50	-1
            5	1	1	1	1	1	100	200	150	50	96.5	the
            5	1	1	1	1	2	300	200	200	50	95.1	ship
            5	1	1	1	1	3	520	200	10	50	12.0
            """;

        var words = OcrRules.ParseTsv(tsv.Replace("            ", ""), 1000, 2000);

        Assert.Equal(["the", "ship"], words.Select(word => word.T));
        Assert.Equal(new OcrWord("the", 0.1, 0.1, 0.15, 0.025), words[0]);
        Assert.Empty(OcrRules.ParseTsv(tsv, 0, 0));
    }

    [Fact]
    public void Pdfinfo_gives_the_page_count()
    {
        Assert.Equal(12, OcrRules.ParsePageCount("Title: x\nPages:           12\nEncrypted: no\n"));
        Assert.Null(OcrRules.ParsePageCount("Syntax Error: not a PDF"));
    }

    [Fact]
    public async Task A_scanned_pdf_gets_words_a_reader_can_select()
    {
        var tools = factory.Services.GetRequiredService<OcrTools>();
        var client = factory.Client;
        var book = await (await client.PostAsJsonAsync("/books", new CreateBookRequest("A Scan", "Someone", BookStatus.Reading, null), JsonOptions))
            .Content.ReadFromJsonAsync<BookResponse>(JsonOptions);

        if (!tools.Available)
        {
            // Without Tesseract and Poppler the page says so instead of waiting forever.
            using var plain = new MultipartFormDataContent { { new ByteArrayContent(TextPdf("Nothing to read.")), "file", "plain.pdf" } };
            await client.PostAsync($"/books/{book!.Id}/ebook", plain);
            var off = await client.GetFromJsonAsync<OcrPageResponse>($"/books/{book.Id}/ebook/ocr/0", JsonOptions);
            Assert.Equal("unavailable", off?.State);
            return;
        }

        using (var content = new MultipartFormDataContent { { new ByteArrayContent(await ScannedPdfAsync("the ship leaves Anarres")), "file", "scan.pdf" } })
        {
            await client.PostAsync($"/books/{book!.Id}/ebook", content);
        }

        OcrPageResponse? page = null;
        for (var attempt = 0; attempt < 120 && page?.State != "done"; attempt++)
        {
            page = await client.GetFromJsonAsync<OcrPageResponse>($"/books/{book.Id}/ebook/ocr/0", JsonOptions);
            if (page?.State != "done")
            {
                await Task.Delay(500);
            }
        }

        Assert.Equal("done", page?.State);
        Assert.Equal(1, page?.Pages);
        var words = page!.Words!.Select(word => word.T.ToLowerInvariant()).ToList();
        Assert.Contains("ship", words);
        Assert.Contains("leaves", words);
        Assert.All(page.Words!, word => Assert.InRange(word.X, 0, 1));

        // Another reader cannot read the words of a book that is not theirs.
        var stranger = await factory.SignUpAsync("Ocr Stranger");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/books/{book.Id}/ebook/ocr/0")).StatusCode);

        // A text PDF needs no reading: its pages have no OCR words.
        var text = await (await client.PostAsJsonAsync("/books", new CreateBookRequest("A Text Pdf", "Someone", BookStatus.Reading, null), JsonOptions))
            .Content.ReadFromJsonAsync<BookResponse>(JsonOptions);
        using (var content = new MultipartFormDataContent { { new ByteArrayContent(TextPdf("This page already has plenty of its own text.")), "file", "text.pdf" } })
        {
            await client.PostAsync($"/books/{text!.Id}/ebook", content);
        }

        OcrPageResponse? own = null;
        for (var attempt = 0; attempt < 120 && own?.State != "done"; attempt++)
        {
            own = await client.GetFromJsonAsync<OcrPageResponse>($"/books/{text.Id}/ebook/ocr/0", JsonOptions);
            if (own?.State != "done")
            {
                await Task.Delay(500);
            }
        }

        Assert.Equal("done", own?.State);
        Assert.Null(own?.Words);
    }

    // A one-page PDF with real text, drawn in Helvetica.
    private static byte[] TextPdf(string line)
    {
        var content = $"BT /F1 28 Tf 72 700 Td ({line}) Tj ET";
        return Pdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
    }

    // The same page as a picture only: render it with Poppler, then wrap the JPEG in a PDF of its own.
    private static async Task<byte[]> ScannedPdfAsync(string line)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"shelf-ocr-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var source = Path.Combine(folder, "text.pdf");
            await File.WriteAllBytesAsync(source, TextPdf(line));
            using (var render = Process.Start(new ProcessStartInfo("pdftoppm", ["-r", "150", "-jpeg", "-singlefile", source, Path.Combine(folder, "page")]))!)
            {
                await render.WaitForExitAsync();
            }

            var jpeg = await File.ReadAllBytesAsync(Path.Combine(folder, "page.jpg"));
            var (width, height) = JpegSize(jpeg);
            const string draw = "q 612 0 0 792 0 0 cm /Im0 Do Q";
            return Pdf(
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>",
                $"<< /Length {draw.Length} >>\nstream\n{draw}\nendstream",
                (Encoding.Latin1.GetBytes($"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n"), jpeg, "\nendstream"u8.ToArray()));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static (int Width, int Height) JpegSize(byte[] jpeg)
    {
        var index = 2;
        while (index + 9 < jpeg.Length)
        {
            if (jpeg[index] != 0xFF)
            {
                index++;
                continue;
            }

            var marker = jpeg[index + 1];
            var length = (jpeg[index + 2] << 8) | jpeg[index + 3];
            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                return ((jpeg[index + 7] << 8) | jpeg[index + 8], (jpeg[index + 5] << 8) | jpeg[index + 6]);
            }

            index += 2 + length;
        }

        throw new InvalidDataException("No size in that JPEG.");
    }

    private static byte[] Pdf(params object[] objects)
    {
        using var output = new MemoryStream();
        void Write(byte[] bytes) => output.Write(bytes);
        Write("%PDF-1.4\n"u8.ToArray());
        var offsets = new List<long>();
        for (var number = 1; number <= objects.Length; number++)
        {
            offsets.Add(output.Position);
            Write(Encoding.Latin1.GetBytes($"{number} 0 obj\n"));
            switch (objects[number - 1])
            {
                case string text:
                    Write(Encoding.Latin1.GetBytes(text));
                    break;
                case ValueTuple<byte[], byte[], byte[]> (var head, var body, var tail):
                    Write(head);
                    Write(body);
                    Write(tail);
                    break;
            }

            Write("\nendobj\n"u8.ToArray());
        }

        var table = output.Position;
        var xref = new StringBuilder($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            xref.Append(System.Globalization.CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        xref.Append(System.Globalization.CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{table}\n%%EOF\n");
        Write(Encoding.Latin1.GetBytes(xref.ToString()));
        return output.ToArray();
    }
}
