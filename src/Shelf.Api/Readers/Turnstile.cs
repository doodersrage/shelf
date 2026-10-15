using System.Text.Json.Serialization;

namespace Shelf.Api.Readers;

// Cloudflare Turnstile on the forms anyone can reach (signing in, signing up, a forgotten password), to keep scripts
// from trying passwords or making accounts. Off until an admin sets Turnstile:SiteKey and Turnstile:SecretKey from a
// widget of their own, and there is a hostname the shelf is reached at to hold answers to: Turnstile:Hostnames, or
// else the host of Email:PublicAddress.
public interface ITurnstile
{
    bool Enabled { get; }

    string? SiteKey { get; }

    // Whether the token the widget put in the form is good: Cloudflare says so, for this form, on this shelf's own
    // address. Anything else, the check failing to finish included, is no.
    Task<bool> VerifyAsync(string? token, string action, string? remoteIp, CancellationToken cancellationToken);
}

public static class TurnstileActions
{
    public const string SignIn = "signin";
    public const string SignUp = "signup";
    public const string Forgot = "forgot";

    // The field the widget adds to its form.
    public const string Field = "cf-turnstile-response";
}

public sealed class Turnstile(HttpClient http, IConfiguration configuration, ILogger<Turnstile> logger) : ITurnstile
{
    private const string Siteverify = "https://challenges.cloudflare.com/turnstile/v0/siteverify";
    private const int MaxTokenLength = 2048;

    public string? SiteKey => configuration["Turnstile:SiteKey"] is { Length: > 0 } key ? key.Trim() : null;

    private string? Secret => configuration["Turnstile:SecretKey"] is { Length: > 0 } secret ? secret.Trim() : null;

    public bool Enabled => SiteKey is not null && Secret is not null && Hostnames(configuration).Count > 0;

    // The hosts the forms are on: those named, or the shelf's own public address.
    public static IReadOnlySet<string> Hostnames(IConfiguration configuration)
    {
        var named = (configuration["Turnstile:Hostnames"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (named.Length > 0)
        {
            return named.Select(host => host.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        }

        return Uri.TryCreate(configuration["Email:PublicAddress"], UriKind.Absolute, out var address)
            ? new HashSet<string>(StringComparer.Ordinal) { address.Host.ToLowerInvariant() }
            : new HashSet<string>(StringComparer.Ordinal);
    }

    public async Task<bool> VerifyAsync(string? token, string action, string? remoteIp, CancellationToken cancellationToken)
    {
        var hostnames = Hostnames(configuration);
        if (!Enabled || string.IsNullOrEmpty(token) || token.Length > MaxTokenLength || hostnames.Count == 0)
        {
            return false;
        }

        var form = new Dictionary<string, string> { ["secret"] = Secret!, ["response"] = token };
        if (!string.IsNullOrEmpty(remoteIp))
        {
            form["remoteip"] = remoteIp;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await http.PostAsync(Siteverify, new FormUrlEncodedContent(form), timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Turnstile's check answered {Status}; the form was refused.", (int)response.StatusCode);
                return false;
            }

            var answer = await response.Content.ReadFromJsonAsync<Answer>(timeout.Token);
            if (answer?.ErrorCodes is { Length: > 0 } codes && codes.Contains("invalid-input-secret"))
            {
                logger.LogWarning("Turnstile does not know Turnstile:SecretKey; every form it guards is refused until it is right.");
            }

            return answer is { Success: true }
                && string.Equals(answer.Action, action, StringComparison.Ordinal)
                && answer.Hostname is { } hostname && hostnames.Contains(hostname.ToLowerInvariant());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // Out of reach, too slow, or an answer that is not Turnstile's: no.
            logger.LogWarning(ex, "Turnstile's check did not finish; the form was refused.");
            return false;
        }
    }

    private sealed record Answer(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("action")] string? Action,
        [property: JsonPropertyName("hostname")] string? Hostname,
        [property: JsonPropertyName("error-codes")] string[]? ErrorCodes);
}
