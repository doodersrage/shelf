using System.Net;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class OutboundGuardTests
{
    [Theory]
    // The open internet: reached either way.
    [InlineData("93.184.216.34", true, true)]
    [InlineData("2606:2800:220:1:248:1893:25c8:1946", true, true)]
    // This machine and the local network: only for notices and webhooks.
    [InlineData("127.0.0.1", false, true)]
    [InlineData("10.0.0.5", false, true)]
    [InlineData("172.16.0.1", false, true)]
    [InlineData("172.31.255.255", false, true)]
    [InlineData("192.168.1.1", false, true)]
    [InlineData("100.64.0.1", false, true)]
    [InlineData("::1", false, true)]
    [InlineData("fd00::1", false, true)]
    [InlineData("::ffff:192.168.1.1", false, true)]
    // A cloud's metadata service, nowhere, and multicast: never.
    [InlineData("169.254.169.254", false, false)]
    [InlineData("::ffff:169.254.169.254", false, false)]
    [InlineData("fe80::1", false, false)]
    [InlineData("0.0.0.0", false, false)]
    [InlineData("224.0.0.1", false, false)]
    // Just outside the private ranges: the internet.
    [InlineData("172.32.0.1", true, true)]
    [InlineData("11.0.0.1", true, true)]
    public void An_address_is_reached_as_far_as_its_kind_allows(string address, bool publicReach, bool localReach)
    {
        Assert.Equal(publicReach, OutboundGuard.Allows(IPAddress.Parse(address), OutboundReach.Public));
        Assert.Equal(localReach, OutboundGuard.Allows(IPAddress.Parse(address), OutboundReach.Local));
    }

    [Fact]
    public async Task A_request_to_this_machine_is_refused_before_it_connects_and_a_local_one_goes()
    {
        using var listener = new HttpListener();
        var port = Random.Shared.Next(20_000, 40_000);
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var answering = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            context.Response.StatusCode = 200;
            context.Response.Close();
        });

        using var open = new HttpClient(OutboundGuard.Handler(OutboundReach.Public));
        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => open.GetAsync($"http://127.0.0.1:{port}/"));
        Assert.True(OutboundGuard.IsRefusal(refused));
        // A name that resolves to this machine is refused the same.
        await Assert.ThrowsAsync<HttpRequestException>(() => open.GetAsync($"http://localhost:{port}/"));

        using var local = new HttpClient(OutboundGuard.Handler(OutboundReach.Local));
        Assert.Equal(HttpStatusCode.OK, (await local.GetAsync($"http://127.0.0.1:{port}/")).StatusCode);
        await answering;
    }
}
