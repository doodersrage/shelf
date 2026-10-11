using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class WordLookupTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task Wiktionary_and_wikipedia_answers_become_a_meaning_in_the_books_language()
    {
        var asked = new ConcurrentBag<string>();
        var handler = new Answers(request =>
        {
            asked.Add(request.RequestUri!.ToString());
            return request.RequestUri!.Host switch
            {
                "en.wiktionary.org" => """{"en":[{"partOfSpeech":"Noun","definitions":[{"definition":"An English <a href=\"/wiki/x\">sense</a>."}]}],"fr":[{"partOfSpeech":"Nom","definitions":[{"definition":"Une <b>bruine</b> légère."},{"definition":""}]}]}""",
                "fr.wikipedia.org" => """{"type":"standard","title":"Bruine","extract":"La bruine est une précipitation.","content_urls":{"desktop":{"page":"https://fr.wikipedia.org/wiki/Bruine"}}}""",
                _ => null,
            };
        });
        var lookup = new WikimediaLookup(new HttpClient(handler));

        var meaning = await lookup.FindAsync("bruine", "fr", CancellationToken.None);
        Assert.Equal(("Nom", "Une bruine légère."), (Assert.Single(meaning!.Senses).PartOfSpeech, meaning.Senses[0].Definition));
        Assert.Equal(("Bruine", "La bruine est une précipitation.", "https://fr.wikipedia.org/wiki/Bruine"), (meaning.SummaryTitle, meaning.Summary, meaning.EncyclopediaUrl));
        Assert.Contains(asked, url => url.EndsWith("/page/definition/bruine", StringComparison.Ordinal));

        Assert.Null(await new WikimediaLookup(new HttpClient(new Answers(_ => null))).FindAsync("nothing", "en", CancellationToken.None));
    }

    [Theory]
    [InlineData("  drizzly, ", "drizzly")]
    [InlineData("“the Pequod”", "the Pequod")]
    [InlineData("a whole sentence that goes on far too long", null)]
    [InlineData("…", null)]
    public void Only_a_word_or_a_short_phrase_is_looked_up(string chosen, string? cleaned) =>
        Assert.Equal(cleaned, WordLookups.Clean(chosen));

    [Fact]
    public async Task The_reader_looks_words_up_in_the_books_language()
    {
        var reader = await factory.SignUpAsync("Word Looker");
        var book = await (await reader.PostAsJsonAsync("/books", new CreateBookRequest("Le Petit Prince", "Antoine de Saint-Exupéry", BookStatus.Reading, null, Language: "French"), JsonOptions)).Content.ReadFromJsonAsync<BookResponse>(JsonOptions);
        factory.Words.Meanings["renard"] = new WordMeaning("renard", "fr", [new WordSense("Nom", "Mammifère carnivore.")], null, null, null, null);

        var meaning = await reader.GetFromJsonAsync<WordMeaning>($"/books/{book!.Id}/look-up?words={Uri.EscapeDataString("renard,")}", JsonOptions);
        Assert.Equal("Mammifère carnivore.", meaning!.Senses[0].Definition);
        Assert.Equal(("renard", "fr"), factory.Words.Asked.Last());
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/books/{book.Id}/look-up?words=nothingatall")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await (await factory.SignUpAsync("Other Looker")).GetAsync($"/books/{book.Id}/look-up?words=renard")).StatusCode);
    }

    private sealed class Answers(Func<HttpRequestMessage, string?> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request) is { } body
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

public sealed class StubWordLookup : IWordLookup
{
    public ConcurrentDictionary<string, WordMeaning> Meanings { get; } = new();

    public ConcurrentQueue<(string Words, string Language)> Asked { get; } = new();

    public Task<WordMeaning?> FindAsync(string words, string language, CancellationToken cancellationToken)
    {
        Asked.Enqueue((words, language));
        return Task.FromResult(Meanings.TryGetValue(words, out var meaning) ? meaning : null);
    }
}
