using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class WelcomeAndCardTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task A_new_shelf_offers_the_ways_to_bring_a_library()
    {
        var reader = await factory.SignUpAsync("Newcomer");
        var home = await reader.GetStringAsync("/");
        Assert.Contains("Where are your books now?", home);
        foreach (var way in new[] { "/import/audiobookshelf", "/import/calibre", "/backup#csv", "/free" })
        {
            Assert.Contains($"href=\"{way}\"", home);
        }

        await reader.PostAsJsonAsync("/books", new CreateBookRequest("Kindred", "Octavia E. Butler", BookStatus.Reading, null), JsonOptions);
        Assert.DoesNotContain("Where are your books now?", await reader.GetStringAsync("/"));
    }

    [Fact]
    public async Task The_year_becomes_a_picture_with_its_numbers_and_books()
    {
        var reader = await factory.SignUpAsync("Card Reader");
        var year = DateTime.UtcNow.Year;
        await reader.PostAsJsonAsync("/books", new CreateBookRequest("The Dispossessed", "Ursula K. Le Guin", BookStatus.Finished, 5, Pages: 387, FinishedOn: new DateOnly(year, 2, 3)), JsonOptions);
        await reader.PostAsJsonAsync("/books", new CreateBookRequest("A Very Long Title That Goes On and On Without Any Cover At All", "Someone", BookStatus.Finished, null, Pages: 120, FinishedOn: new DateOnly(year, 3, 4)), JsonOptions);

        var response = await reader.GetAsync($"/books/years/{year}/card.svg");
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
        var svg = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("<svg", svg);
        Assert.Contains($"My {year} in books", svg);
        Assert.Contains("books finished", svg);
        Assert.Contains("507", svg);
        Assert.Contains("The Dispossessed", svg);
        Assert.DoesNotContain("foreignObject", svg);
        Assert.Contains("Share my year", await reader.GetStringAsync($"/years/{year}"));
    }

    // A cover linked from this machine or its network is never fetched for the card unless an admin allows such
    // addresses, so a reader can't use the card to look inside the network the shelf runs in.
    [Fact]
    public async Task A_cover_linked_from_this_machine_is_not_fetched_for_the_card()
    {
        var picture = Png("on this machine");
        using var server = new CoverServer(_ => (picture, false));
        var reader = await factory.SignUpAsync("Card Prober");
        var year = DateTime.UtcNow.Year;
        await reader.PostAsJsonAsync("/books", new CreateBookRequest("Inside Job", "Someone", BookStatus.Finished, null, CoverUrl: server.Address, FinishedOn: new DateOnly(year, 1, 2)), JsonOptions);

        var response = await reader.GetAsync($"/books/years/{year}/card.svg");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var svg = await response.Content.ReadAsStringAsync();
        Assert.Contains("Inside Job", svg);
        Assert.DoesNotContain(Convert.ToBase64String(picture), svg);
        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public async Task With_local_addresses_allowed_a_local_cover_is_drawn_but_only_a_picture_of_a_cover_s_size()
    {
        await using var app = factory.WithWebHostBuilder(builder => builder.UseSetting("Network:AllowPrivateAddresses", "true"));
        var picture = Png("on the local network");
        var notPicture = System.Text.Encoding.UTF8.GetBytes("{\"secret\":\"not for a card\"}");
        // Bigger than any cover, sent with no length up front, so only reading it says how big it is.
        var huge = new byte[CoverStore.MaxBytes + 100_000];
        Png("huge").CopyTo(huge, 0);
        using var server = new CoverServer(path => path switch
        {
            "/picture" => (picture, false),
            "/text" => (notPicture, false),
            _ => (huge, true),
        });

        var reader = app.CreateClient();
        await ShelfApiFactory.PostFormAsync(reader, "/signup", "/account/signup", new() { ["name"] = "Local Card Reader", ["email"] = ShelfApiFactory.EmailOf("Local Card Reader"), ["password"] = ShelfApiFactory.Password, ["confirm"] = ShelfApiFactory.Password });
        var year = DateTime.UtcNow.Year;
        foreach (var (title, path) in new[] { ("Picture", "/picture"), ("Text", "/text"), ("Huge", "/huge") })
        {
            await reader.PostAsJsonAsync("/books", new CreateBookRequest(title, "Someone", BookStatus.Finished, null, CoverUrl: server.Address + path.TrimStart('/'), FinishedOn: new DateOnly(year, 1, 2)), JsonOptions);
        }

        var response = await reader.GetAsync($"/books/years/{year}/card.svg");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var svg = await response.Content.ReadAsStringAsync();
        Assert.Contains($"data:image/png;base64,{Convert.ToBase64String(picture)}", svg);
        Assert.DoesNotContain(Convert.ToBase64String(notPicture), svg);
        Assert.DoesNotContain("data:application", svg);
        Assert.True(svg.Length < CoverStore.MaxBytes, "The oversized cover went into the card.");
        Assert.Equal(3, server.Requests);
    }

    // The first bytes of a PNG, then something to tell one from another.
    private static byte[] Png(string mark) => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, .. System.Text.Encoding.UTF8.GetBytes(mark)];

    // A little web server on this machine answering each path with the bytes chosen for it, chunked if asked.
    private sealed class CoverServer : IDisposable
    {
        private readonly System.Net.HttpListener _listener = new();
        private int _requests;

        public CoverServer(Func<string, (byte[] Body, bool Chunked)> answer)
        {
            var port = Random.Shared.Next(20_000, 40_000);
            Address = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Address);
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    System.Net.HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception ex) when (ex is System.Net.HttpListenerException or ObjectDisposedException or InvalidOperationException)
                    {
                        return;
                    }

                    Interlocked.Increment(ref _requests);
                    var (body, chunked) = answer(context.Request.Url!.AbsolutePath);
                    try
                    {
                        context.Response.SendChunked = chunked;
                        if (!chunked)
                        {
                            context.Response.ContentLength64 = body.Length;
                        }

                        await context.Response.OutputStream.WriteAsync(body);
                        context.Response.Close();
                    }
                    catch (Exception ex) when (ex is System.Net.HttpListenerException or IOException or ObjectDisposedException)
                    {
                        // The shelf stopped reading, as it should for a body too big.
                    }
                }
            });
        }

        public string Address { get; }

        public int Requests => Volatile.Read(ref _requests);

        public void Dispose() => _listener.Close();
    }

    [Fact]
    public void A_spine_title_wraps_and_is_cut_short_when_it_runs_over()
    {
        Assert.Equal(["A Wizard of", "Earthsea"], YearCard.Wrap("A Wizard of Earthsea", 13, 6));
        var long_ = YearCard.Wrap(string.Join(' ', Enumerable.Repeat("word", 40)), 13, 3);
        Assert.Equal(3, long_.Count);
        Assert.EndsWith("…", long_[^1]);
    }
}
