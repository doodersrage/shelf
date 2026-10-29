using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class LeftoverFileTests
{
    // A file from a linked shelf that turns out too big, or is cut off, leaves nothing in the temporary folder; the
    // whole one is kept for the caller.
    [Fact]
    public async Task A_linked_shelf_download_that_fails_leaves_no_file()
    {
        static string[] Leftovers() => Directory.GetFiles(Path.GetTempPath(), "shelf-link-*");
        var before = Leftovers().ToHashSet();
        var links = new LinkClient(new Clients(new Sends(new byte[5000])), new EphemeralDataProtectionProvider());
        var link = new ShelfLink { Name = "Next door", Address = "https://next-door.example" };

        Assert.Null(await links.DownloadAsync(link, "/file", 1000, CancellationToken.None));
        Assert.Empty(Leftovers().Where(path => !before.Contains(path)));

        var cut = new LinkClient(new Clients(new Sends(new byte[5000], cutAfter: 2000)), new EphemeralDataProtectionProvider());
        Assert.Null(await cut.DownloadAsync(link, "/file", 10_000, CancellationToken.None));
        Assert.Empty(Leftovers().Where(path => !before.Contains(path)));

        var whole = await links.DownloadAsync(link, "/file", 10_000, CancellationToken.None);
        Assert.Equal(5000, new FileInfo(whole!).Length);
        File.Delete(whole!);
    }

    // A linked shelf that never answers is given up on after a few seconds, not the client's hour, so it can't hold
    // the Shelves or Loans page.
    [Fact]
    public async Task A_linked_shelf_that_never_answers_is_given_up_on_soon()
    {
        var links = new LinkClient(new Clients(new Hangs()), new EphemeralDataProtectionProvider());
        var link = new ShelfLink { Name = "Asleep", Address = "https://asleep.example" };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var asked = links.GetAsync<LinkedShelf[]>(link, "/federation/shelves", CancellationToken.None);
        var sent = links.SendAsync(link, HttpMethod.Post, "/federation/asks", null, CancellationToken.None);
        Assert.Null(await asked);
        Assert.False(await sent);
        Assert.InRange(clock.Elapsed, LinkClient.Quick - TimeSpan.FromSeconds(1), LinkClient.Quick + TimeSpan.FromSeconds(10));
    }

    private sealed class Hangs : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    // Answers with the bytes and no length given, as a streamed answer does; cut off partway when asked.
    private sealed class Sends(byte[] body, int? cutAfter = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Breaking(body, cutAfter)) });
    }

    private sealed class Breaking(byte[] body, int? cutAfter) : MemoryStream(body)
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            cutAfter is int cut && Position >= cut ? throw new IOException("The connection was cut.") : base.Read(buffer, offset, Math.Min(count, 500));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            cutAfter is int cut && Position >= cut ? throw new IOException("The connection was cut.") : base.ReadAsync(buffer[..Math.Min(buffer.Length, 500)], cancellationToken);
    }
}
