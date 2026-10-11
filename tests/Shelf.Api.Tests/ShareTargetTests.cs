using System.Net;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class ShareTargetTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    [Theory]
    [InlineData(null, "Look at this: ISBN 978-0-441-47812-5", null, "/?add=1&isbn=9780441478125")]
    [InlineData("The Left Hand of Darkness", null, "https://openlibrary.org/isbn/0441478123", "/?add=1&isbn=0441478123")]
    [InlineData("The Dispossessed by Ursula K. Le Guin | Goodreads", "https://www.goodreads.com/book/show/13651", null, "/?add=1&title=The%20Dispossessed&author=Ursula%20K.%20Le%20Guin")]
    [InlineData("Piranesi: Amazon.co.uk: Clarke, Susanna", null, "https://www.amazon.co.uk/dp/B08L9MSJSC", "/?add=1&title=Piranesi")]
    [InlineData(null, null, null, "/?add=1")]
    public void What_is_shared_becomes_the_add_forms_address(string? title, string? text, string? url, string target) =>
        Assert.Equal(target, ShareTarget.Target(title, text, url));

    [Fact]
    public async Task A_share_opens_the_add_form_filled_in()
    {
        var reader = await factory.SignUpAsync("Sharing In");
        var answer = await reader.GetAsync("/share?text=" + Uri.EscapeDataString("Read this: 978-0-441-47812-5"));
        Assert.Equal("/", answer.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Contains("isbn=9780441478125", answer.RequestMessage.RequestUri.Query);

        var form = await reader.GetStringAsync("/?add=1&title=" + Uri.EscapeDataString("The Telling") + "&author=" + Uri.EscapeDataString("Ursula K. Le Guin"));
        Assert.Contains("value=\"The Telling\"", form);
        Assert.Contains("value=\"Ursula K. Le Guin\"", form);
    }
}
