using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

// Shelf admin: who may sign up, invitations, suspending, renaming, signing out, the announcement, and the jobs. Tenar,
// the factory's first reader, is the admin.
public sealed partial class ManagementTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task An_invitation_lets_its_address_in_when_only_the_invited_may_sign_up()
    {
        var admin = factory.Client;
        (await admin.PutAsJsonAsync("/admin/signup", new SignUpModeRequest(SignUpMode.Invites), JsonOptions)).EnsureSuccessStatusCode();
        try
        {
            var stranger = factory.CreateClient(new() { AllowAutoRedirect = false });
            var refused = await ShelfApiFactory.PostFormAsync(stranger, "/signin", "/account/signup", new()
            {
                ["name"] = "Uninvited",
                ["email"] = "uninvited@example.test",
                ["password"] = ShelfApiFactory.Password,
                ["confirm"] = ShelfApiFactory.Password,
            });
            Assert.Contains("problem=SignUpClosed", refused.Headers.Location?.OriginalString);
            Assert.Contains("by invitation", await stranger.GetStringAsync("/signup"));

            var made = await (await admin.PostAsJsonAsync("/admin/invitations", new InvitationRequest("guest@example.test, not-an-address\ntenar@example.test"), JsonOptions))
                .Content.ReadFromJsonAsync<InvitationMade[]>(JsonOptions);
            Assert.Equal(3, made!.Length);
            Assert.True(made.Single(item => item.Email == "guest@example.test").Sent);
            Assert.NotNull(made.Single(item => item.Email == "not-an-address").Problem);
            Assert.Contains("already signs in", made.Single(item => item.Email == "tenar@example.test").Problem);

            var mail = factory.Mail.Sent.Last(item => item.To == "guest@example.test");
            Assert.Equal("You're invited to Shelf", mail.Subject);
            var token = InviteLink().Match(mail.Body).Groups[1].Value;
            var page = await stranger.GetStringAsync($"/signup?invite={token}");
            Assert.Contains("You are invited", page);
            Assert.Contains("value=\"guest@example.test\"", page);

            // The address is the invitation's, whatever the form says, and it is confirmed already.
            var joined = await ShelfApiFactory.PostFormAsync(stranger, $"/signup?invite={token}", "/account/signup", new()
            {
                ["name"] = "Invited Guest",
                ["email"] = "someone.else@example.test",
                ["password"] = ShelfApiFactory.Password,
                ["confirm"] = ShelfApiFactory.Password,
                ["invite"] = token,
            });
            Assert.Equal("/", joined.Headers.Location?.OriginalString);
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
                Assert.Equal("Invited Guest", (await ReaderRules.FindAsync(db, "guest@example.test"))?.Name);
            }

            var invitations = await admin.GetFromJsonAsync<InvitationSummary[]>("/admin/invitations", JsonOptions);
            Assert.Equal("Invited Guest", invitations!.Single(item => item.Email == "guest@example.test").Reader);

            var again = await ShelfApiFactory.PostFormAsync(factory.CreateClient(new() { AllowAutoRedirect = false }), "/signin", "/account/signup", new()
            {
                ["name"] = "Second Try",
                ["email"] = "guest2@example.test",
                ["password"] = ShelfApiFactory.Password,
                ["confirm"] = ShelfApiFactory.Password,
                ["invite"] = token,
            });
            Assert.Contains("problem=InviteExpired", again.Headers.Location?.OriginalString);
        }
        finally
        {
            await admin.PutAsJsonAsync("/admin/signup", new SignUpModeRequest(SignUpMode.Open), JsonOptions);
        }
    }

    [Fact]
    public async Task A_suspended_reader_cannot_sign_in_by_any_way_until_restored()
    {
        var reader = await factory.SignUpAsync("Paused Reader");
        var readerId = await factory.ReaderIdAsync("Paused Reader");
        var made = await (await reader.PostAsJsonAsync("/account/tokens", new NewApiTokenRequest("script", false), JsonOptions)).Content.ReadFromJsonAsync<NewApiToken>(JsonOptions);
        var script = factory.CreateClient();
        script.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", made!.Token);
        Assert.Equal(HttpStatusCode.OK, (await script.GetAsync("/books")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await factory.Client.PutAsJsonAsync($"/admin/readers/{await factory.ReaderIdAsync("Tenar")}/suspended", new SuspendRequest(true), JsonOptions)).StatusCode);
        (await factory.Client.PutAsJsonAsync($"/admin/readers/{readerId}/suspended", new SuspendRequest(true), JsonOptions)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await reader.GetAsync("/books")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await script.GetAsync("/books")).StatusCode);
        var signIn = await ShelfApiFactory.PostFormAsync(factory.CreateClient(new() { AllowAutoRedirect = false }), "/signin", "/account/signin", new()
        {
            ["email"] = ShelfApiFactory.EmailOf("Paused Reader"),
            ["password"] = ShelfApiFactory.Password,
        });
        Assert.Contains("problem=Suspended", signIn.Headers.Location?.OriginalString);
        Assert.Contains((await factory.Client.GetFromJsonAsync<ReaderRow[]>("/admin/accounts", JsonOptions))!, row => row.Id == readerId && row.Suspended);

        (await factory.Client.PutAsJsonAsync($"/admin/readers/{readerId}/suspended", new SuspendRequest(false), JsonOptions)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await script.GetAsync("/books")).StatusCode);
        var back = await ShelfApiFactory.PostFormAsync(factory.CreateClient(new() { AllowAutoRedirect = false }), "/signin", "/account/signin", new()
        {
            ["email"] = ShelfApiFactory.EmailOf("Paused Reader"),
            ["password"] = ShelfApiFactory.Password,
        });
        Assert.Equal("/", back.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task An_admin_renames_a_reader_changes_their_address_and_signs_them_out_without_a_new_password()
    {
        var reader = await factory.SignUpAsync("Renamed Reader");
        var readerId = await factory.ReaderIdAsync("Renamed Reader");
        (await factory.Client.PutAsJsonAsync($"/admin/readers/{readerId}/name", new RenameRequest("Better Name"), JsonOptions)).EnsureSuccessStatusCode();
        (await factory.Client.PutAsJsonAsync($"/admin/readers/{readerId}/email", new KindleSettingsRequest("better@example.test"), JsonOptions)).EnsureSuccessStatusCode();
        var row = (await factory.Client.GetFromJsonAsync<ReaderRow[]>("/admin/accounts", JsonOptions))!.Single(item => item.Id == readerId);
        Assert.Equal(("Better Name", "better@example.test"), (row.Name, row.Email));

        (await factory.Client.PostAsync($"/admin/readers/{readerId}/signout", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await reader.GetAsync("/books")).StatusCode);
        await IdleAsync();
        Assert.DoesNotContain(factory.Mail.Sent, mail => mail.To == "better@example.test" && mail.Subject == "Your Shelf password changed");
    }

    [Fact]
    public async Task An_announcement_is_on_every_page_until_it_is_taken_down_and_can_go_by_email()
    {
        var reader = await factory.SignUpAsync("Announced To");
        var answer = await (await factory.Client.PutAsJsonAsync("/admin/announcement", new AnnouncementRequest("The shelf is down for an hour on Sunday.", Email: true), JsonOptions))
            .Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.True(answer.GetProperty("emailed").GetInt32() >= 2);
        Assert.Contains("The shelf is down for an hour on Sunday.", await reader.GetStringAsync("/library"));
        Assert.Contains(factory.Mail.Sent, mail => mail.To == ShelfApiFactory.EmailOf("Announced To") && mail.Subject == "News from your shelf" && mail.Body.Contains("down for an hour", StringComparison.Ordinal));

        (await factory.Client.PutAsJsonAsync("/admin/announcement", new AnnouncementRequest(null), JsonOptions)).EnsureSuccessStatusCode();
        Assert.DoesNotContain("down for an hour", await reader.GetStringAsync("/library"));
    }

    [Fact]
    public async Task An_admin_runs_a_job_now_and_only_an_admin_manages_the_shelf()
    {
        var jobs = await factory.Client.GetFromJsonAsync<JsonElement[]>("/admin/jobs", JsonOptions);
        Assert.Contains(jobs!, job => job.GetProperty("key").GetString() == "notices");
        Assert.Equal(HttpStatusCode.NoContent, (await factory.Client.PostAsync("/admin/jobs/notices", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.Client.PostAsync("/admin/jobs/no-such-job", null)).StatusCode);

        var reader = await factory.SignUpAsync("Not An Admin");
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/admin/invitations", new InvitationRequest("someone@example.test"), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync("/admin/announcement", new AnnouncementRequest("Hello"), JsonOptions)).StatusCode);
        Assert.Contains("Only an admin", await reader.GetStringAsync("/admin"));
    }

    private async Task IdleAsync()
    {
        var events = factory.Services.GetRequiredService<ShelfEvents>();
        for (var i = 0; i < 100 && !events.Idle; i++)
        {
            await Task.Delay(50);
        }
    }

    [GeneratedRegex(@"/signup\?invite=([A-Za-z0-9_-]+)")]
    private static partial Regex InviteLink();
}
