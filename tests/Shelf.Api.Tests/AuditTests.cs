using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

public sealed class AuditTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task Account_changes_and_failed_sign_ins_are_kept_for_the_admin_and_the_reader()
    {
        var admin = factory.Client;
        var reader = await factory.SignUpAsync("Logged Reader");
        var readerId = await factory.ReaderIdAsync("Logged Reader");

        // A wrong password for a real name is noted; a name nobody has is not.
        var visitor = factory.CreateClient();
        await ShelfApiFactory.PostFormAsync(visitor, "/signin", "/account/signin", new() { ["name"] = "Logged Reader", ["password"] = "not the password" });
        await ShelfApiFactory.PostFormAsync(visitor, "/signin", "/account/signin", new() { ["name"] = "Nobody At All", ["password"] = "not the password" });

        await admin.PutAsJsonAsync($"/admin/readers/{readerId}/admin", new AdminChange(true), JsonOptions);
        await admin.PutAsJsonAsync($"/admin/readers/{readerId}/admin", new AdminChange(false), JsonOptions);
        await admin.PostAsync($"/admin/readers/{readerId}/password", null);

        var lines = await admin.GetFromJsonAsync<AuditLine[]>("/admin/audit", JsonOptions);
        Assert.Contains(lines!, line => line.Actor == "Tenar" && line.Action == "Signed up, the first reader and admin");
        Assert.Contains(lines!, line => line.Actor == "Logged Reader" && line.Action == "Signed up");
        Assert.Contains(lines!, line => line.Actor == "Logged Reader" && line.Action == "A sign-in failed: wrong password");
        Assert.DoesNotContain(lines!, line => line.Actor == "Nobody At All" || line.Target == "Nobody At All");
        Assert.Contains(lines!, line => line.Actor == "Tenar" && line.Action == "Made an admin" && line.Target == "Logged Reader");
        Assert.Contains(lines!, line => line.Actor == "Tenar" && line.Action == "Took away admin rights" && line.Target == "Logged Reader");
        Assert.Contains(lines!, line => line.Actor == "Tenar" && line.Action == "Gave a new password" && line.Target == "Logged Reader");
        Assert.DoesNotContain(lines!, line => line.Action == "Turned off two-step sign-in");
        Assert.True(lines!.Select(line => line.At).SequenceEqual(lines!.Select(line => line.At).OrderByDescending(at => at)));

        // Only an admin reads the whole log; a reader sees their own account's lines.
        var stranger = await factory.SignUpAsync("Log Stranger");
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync("/admin/audit")).StatusCode);
        var theirs = WebUtility.HtmlDecode(await stranger.GetStringAsync("/account"));
        Assert.Contains("Recent activity", theirs);
        var section = theirs[theirs.IndexOf("Recent activity", StringComparison.Ordinal)..];
        Assert.Contains("signed up", section);
        Assert.DoesNotContain("Logged Reader", theirs);
        Assert.Contains("gave a new password", WebUtility.HtmlDecode(await admin.GetStringAsync("/admin")));
    }


    [Fact]
    public async Task Only_the_newest_entries_are_kept()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        db.AuditEntries.AddRange(Enumerable.Range(0, Audit.Keep + 25).Select(n => new AuditEntry { At = DateTimeOffset.UtcNow, Action = $"Filler {n}" }));
        await db.SaveChangesAsync();
        Assert.True(await Audit.TrimAsync(db) >= 25);
        Assert.Equal(Audit.Keep, db.AuditEntries.Count());
        Assert.Contains(db.AuditEntries, entry => entry.Action == $"Filler {Audit.Keep + 24}");
    }
}
