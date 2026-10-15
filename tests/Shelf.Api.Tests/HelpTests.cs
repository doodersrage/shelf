using System.Net;
using System.Text.RegularExpressions;

namespace Shelf.Api.Tests;

// The way from the app to its documentation: the Help page, the "?" beside a page's heading, and Server health's
// links to each fix. Every place linked to must be a page of the documentation with that heading.
public sealed partial class HelpTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly string Content = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "site", "content"));

    [Fact]
    public void Every_place_the_app_links_to_is_in_the_documentation()
    {
        var missing = new List<string>();
        foreach (var (page, section) in Docs.Linked)
        {
            var path = Path.Combine(Content, $"{page}.md");
            if (!File.Exists(path))
            {
                missing.Add($"{page}.md");
                continue;
            }

            if (section is not null && !Headings(File.ReadAllText(path)).Contains(section))
            {
                missing.Add($"{page}#{section}");
            }
        }

        Assert.True(missing.Count == 0, "Not in the documentation: " + string.Join(", ", missing));
        Assert.Equal("https://doodersrage.github.io/shelf/devices.html#kobo-e-readers", Docs.Url(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), "devices", "kobo-e-readers"));
    }

    [Fact]
    public async Task Help_lists_the_guides_and_who_to_ask_and_pages_link_to_their_own_guide()
    {
        var reader = await factory.SignUpAsync("Lost Reader");
        var help = WebUtility.HtmlDecode(await reader.GetStringAsync("/help"));
        Assert.Contains("href=\"https://doodersrage.github.io/shelf/reading.html\"", help);
        Assert.Contains("Tenar looks after this shelf", help);
        Assert.DoesNotContain("configuration.html", help);

        // Admins see the guides to running a shelf too.
        var admin = WebUtility.HtmlDecode(await factory.Client.GetStringAsync("/help"));
        Assert.Contains("configuration.html", admin);

        // The menu, everywhere, and a page's own guide beside its heading.
        Assert.Contains("href=\"/help\"", await reader.GetStringAsync("/loans"));
        Assert.Contains("lending.html#lending-to-another-reader", await reader.GetStringAsync("/loans"));
        Assert.Contains("devices.html", await reader.GetStringAsync("/sync"));
        Assert.Contains("security.html#signing-in", await factory.CreateClient().GetStringAsync("/signin"));
        Assert.Contains("How to set this up", await factory.Client.GetStringAsync("/admin/health"));
    }

    // The anchors build.mjs gives the headings of a page: its words lower-cased, joined by hyphens.
    private static HashSet<string> Headings(string markdown) =>
        HeadingLine().Matches(markdown).Select(match =>
        {
            var text = match.Groups[1].Value.Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal);
            text = LinkText().Replace(text, "$1").Replace("&", "&amp;", StringComparison.Ordinal).Replace("'", "&#39;", StringComparison.Ordinal);
            text = Entity().Replace(text.ToLowerInvariant(), "");
            return NotWord().Replace(text, "-").Trim('-');
        }).ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"^#{1,6} (.+)$", RegexOptions.Multiline)]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex LinkText();

    [GeneratedRegex(@"&[a-z]+;")]
    private static partial Regex Entity();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NotWord();
}
