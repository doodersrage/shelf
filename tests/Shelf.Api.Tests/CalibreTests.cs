using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class CalibreTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    private readonly string library = Path.Combine(Path.GetTempPath(), $"shelf-calibre-{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(library))
        {
            Directory.Delete(library, recursive: true);
        }
    }

    [Fact]
    public async Task A_calibre_library_comes_across_with_its_files_covers_series_tags_and_ratings()
    {
        Build();
        var books = CalibreLibrary.Read(library);
        // In Calibre's own order: by author, then by the title without its article.
        Assert.Equal(["The Lathe of Heaven", "The Left Hand of Darkness", "A Wizard of Earthsea"], books.Select(book => book.Title));
        var left = books[1];
        Assert.Equal("Ursula K. Le Guin & Anonymous Editor", left.Author);
        Assert.Equal(("Hainish Cycle", 4.0, 5, "9780441478125", "en", 1969), (left.Series, left.SeriesIndex, left.Rating, left.Isbn, left.Language, left.Year));
        Assert.Equal("A lone envoy comes to a winter world.\n\nIt does not go as planned.", left.Comments);
        Assert.Equal(["science fiction", "Classics"], left.Tags);
        Assert.Equal((null, (int?)null), (books[0].Isbn, books[0].Year));

        var client = await factory.SignUpAsync("Calibre Owner");
        var readerId = await factory.ReaderIdAsync("Calibre Owner");
        var importer = factory.Services.GetRequiredService<CalibreImporter>();
        var job = new CalibreJob(Guid.NewGuid(), readerId, library, books);
        await importer.RunAsync(job, CancellationToken.None);

        Assert.All(job.Lines.Values, line => Assert.Equal(CalibreState.Done, line.State));
        var shelf = await client.GetFromJsonAsync<List<BookResponse>>("/books", JsonOptions);
        var imported = shelf!.Single(book => book.Title == "The Left Hand of Darkness");
        Assert.Equal(("Ursula K. Le Guin & Anonymous Editor", "Hainish Cycle", 4, 5, "9780441478125", 1969), (imported.Author, imported.Series, imported.SeriesNumber, imported.Rating, imported.Isbn, imported.Year));
        Assert.Equal("left-hand.epub", imported.EbookFileName.Replace("The Left Hand of Darkness - Ursula K. Le Guin.epub", "left-hand.epub"));
        Assert.Equal(["classics", "science fiction"], imported.Tags.Order());
        var cover = await client.GetAsync($"/books/{imported.Id}/cover");
        Assert.Equal(System.Net.HttpStatusCode.OK, cover.StatusCode);
        Assert.Equal(Png, await cover.Content.ReadAsByteArrayAsync());
        Assert.Equal("A lone envoy comes to a winter world.\n\nIt does not go as planned.", imported.Notes);

        // No file Shelf takes, or the file is missing from the folder: still a catalog entry.
        var lathe = shelf.Single(book => book.Title == "The Lathe of Heaven");
        Assert.Null(lathe.EbookFileName);
        Assert.Equal(3, lathe.Rating);
        Assert.Null(shelf.Single(book => book.Title == "A Wizard of Earthsea").EbookFileName);

        // A second run brings nothing twice.
        var again = new CalibreJob(Guid.NewGuid(), readerId, library, CalibreLibrary.Read(library));
        await importer.RunAsync(again, CancellationToken.None);
        Assert.All(again.Lines.Values, line => Assert.Equal(CalibreState.Skipped, line.State));
        Assert.Equal(3, (await client.GetFromJsonAsync<List<BookResponse>>("/books", JsonOptions))!.Count);
    }

    [Fact]
    public void A_folder_that_is_not_a_calibre_library_says_so()
    {
        Directory.CreateDirectory(library);
        Assert.Contains("metadata.db", Assert.Throws<CalibreProblem>(() => CalibreLibrary.Read(library)).Message);
        File.WriteAllText(CalibreLibrary.Database(library), "not a database");
        Assert.Throws<CalibreProblem>(() => CalibreLibrary.Read(library));
    }

    // The tables Shelf reads, as Calibre's resources/metadata_sqlite.sql makes them, with three books.
    private void Build()
    {
        Directory.CreateDirectory(library);
        using (var connection = new SqliteConnection($"Data Source={CalibreLibrary.Database(library)}"))
        {
            connection.Open();
            Run(connection, """
                CREATE TABLE books ( id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT NOT NULL DEFAULT 'Unknown' COLLATE NOCASE, sort TEXT COLLATE NOCASE,
                    timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP, pubdate TIMESTAMP DEFAULT CURRENT_TIMESTAMP, series_index REAL NOT NULL DEFAULT 1.0,
                    author_sort TEXT COLLATE NOCASE, path TEXT NOT NULL DEFAULT '', uuid TEXT, has_cover BOOL DEFAULT 0,
                    last_modified TIMESTAMP NOT NULL DEFAULT '2000-01-01 00:00:00+00:00');
                CREATE TABLE authors ( id INTEGER PRIMARY KEY, name TEXT NOT NULL COLLATE NOCASE, sort TEXT COLLATE NOCASE, link TEXT NOT NULL DEFAULT '', UNIQUE(name));
                CREATE TABLE books_authors_link ( id INTEGER PRIMARY KEY, book INTEGER NOT NULL, author INTEGER NOT NULL, UNIQUE(book, author));
                CREATE TABLE series ( id INTEGER PRIMARY KEY, name TEXT NOT NULL COLLATE NOCASE, sort TEXT COLLATE NOCASE, link TEXT NOT NULL DEFAULT '', UNIQUE (name));
                CREATE TABLE books_series_link ( id INTEGER PRIMARY KEY, book INTEGER NOT NULL, series INTEGER NOT NULL, UNIQUE(book));
                CREATE TABLE tags ( id INTEGER PRIMARY KEY, name TEXT NOT NULL COLLATE NOCASE, link TEXT NOT NULL DEFAULT '', UNIQUE (name));
                CREATE TABLE books_tags_link ( id INTEGER PRIMARY KEY, book INTEGER NOT NULL, tag INTEGER NOT NULL, UNIQUE(book, tag));
                CREATE TABLE publishers ( id INTEGER PRIMARY KEY, name TEXT NOT NULL COLLATE NOCASE, sort TEXT COLLATE NOCASE, link TEXT NOT NULL DEFAULT '', UNIQUE(name));
                CREATE TABLE books_publishers_link ( id INTEGER PRIMARY KEY, book INTEGER NOT NULL, publisher INTEGER NOT NULL, UNIQUE(book));
                CREATE TABLE ratings ( id INTEGER PRIMARY KEY, rating INTEGER CHECK(rating > -1 AND rating < 11), link TEXT NOT NULL DEFAULT '', UNIQUE (rating));
                CREATE TABLE books_ratings_link ( id INTEGER PRIMARY KEY, book INTEGER NOT NULL, rating INTEGER NOT NULL, UNIQUE(book, rating));
                CREATE TABLE comments ( id INTEGER PRIMARY KEY, book INTEGER NOT NULL, text TEXT NOT NULL COLLATE NOCASE, UNIQUE(book));
                CREATE TABLE identifiers ( id INTEGER PRIMARY KEY, book INTEGER NOT NULL, type TEXT NOT NULL DEFAULT 'isbn' COLLATE NOCASE, val TEXT NOT NULL COLLATE NOCASE, UNIQUE(book, type));
                CREATE TABLE languages ( id INTEGER PRIMARY KEY, lang_code TEXT NOT NULL COLLATE NOCASE, link TEXT NOT NULL DEFAULT '', UNIQUE(lang_code));
                CREATE TABLE books_languages_link ( id INTEGER PRIMARY KEY, book INTEGER NOT NULL, lang_code INTEGER NOT NULL, item_order INTEGER NOT NULL DEFAULT 0, UNIQUE(book, lang_code));
                CREATE TABLE data ( id INTEGER PRIMARY KEY, book INTEGER NOT NULL, format TEXT NOT NULL COLLATE NOCASE, uncompressed_size INTEGER NOT NULL, name TEXT NOT NULL, UNIQUE(book, format));

                INSERT INTO authors (id, name, sort) VALUES (1, 'Ursula K. Le Guin', 'Le Guin, Ursula K.'), (2, 'Anonymous Editor', 'Editor, Anonymous');
                INSERT INTO books (id, title, sort, pubdate, series_index, author_sort, path, has_cover) VALUES
                    (1, 'The Left Hand of Darkness', 'Left Hand of Darkness, The', '1969-03-01 00:00:00+00:00', 4.0, 'Le Guin, Ursula K.', 'Ursula K. Le Guin/The Left Hand of Darkness (1)', 1),
                    (2, 'The Lathe of Heaven', 'Lathe of Heaven, The', '0101-01-01 00:00:00+00:00', 1.0, 'Le Guin, Ursula K.', 'Ursula K. Le Guin/The Lathe of Heaven (2)', 0),
                    (3, 'A Wizard of Earthsea', 'Wizard of Earthsea, A', '1968-01-01 00:00:00+00:00', 1.0, 'Le Guin, Ursula K.', 'Ursula K. Le Guin/A Wizard of Earthsea (3)', 0);
                INSERT INTO books_authors_link (book, author) VALUES (1, 1), (1, 2), (2, 1), (3, 1);
                INSERT INTO series (id, name) VALUES (1, 'Hainish Cycle');
                INSERT INTO books_series_link (book, series) VALUES (1, 1);
                INSERT INTO tags (id, name) VALUES (1, 'science fiction'), (2, 'Classics');
                INSERT INTO books_tags_link (book, tag) VALUES (1, 1), (1, 2);
                INSERT INTO ratings (id, rating) VALUES (1, 10), (2, 6);
                INSERT INTO books_ratings_link (book, rating) VALUES (1, 1), (2, 2);
                INSERT INTO comments (book, text) VALUES (1, '<div><p>A lone envoy comes to a <i>winter</i> world.</p><p>It does not go as planned.</p></div>');
                INSERT INTO identifiers (book, type, val) VALUES (1, 'isbn', '9780441478125'), (1, 'goodreads', '18423');
                INSERT INTO languages (id, lang_code) VALUES (1, 'eng');
                INSERT INTO books_languages_link (book, lang_code, item_order) VALUES (1, 1, 0);
                INSERT INTO data (book, format, uncompressed_size, name) VALUES
                    (1, 'EPUB', 1000, 'The Left Hand of Darkness - Ursula K. Le Guin'),
                    (1, 'TXT', 10, 'The Left Hand of Darkness - Ursula K. Le Guin'),
                    (3, 'EPUB', 1000, 'A Wizard of Earthsea - Ursula K. Le Guin');
                """);
        }

        var left = Path.Combine(library, "Ursula K. Le Guin", "The Left Hand of Darkness (1)");
        Directory.CreateDirectory(left);
        File.WriteAllBytes(Path.Combine(left, "The Left Hand of Darkness - Ursula K. Le Guin.epub"), ImportTests.Epub("Some Other Title", "Somebody", null, "en", cover: false));
        File.WriteAllText(Path.Combine(left, "The Left Hand of Darkness - Ursula K. Le Guin.txt"), "plain");
        File.WriteAllBytes(Path.Combine(left, "cover.jpg"), Png);
        // Book 3's EPUB is listed but missing from its folder.
        Directory.CreateDirectory(Path.Combine(library, "Ursula K. Le Guin", "A Wizard of Earthsea (3)"));
    }

    private static void Run(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
