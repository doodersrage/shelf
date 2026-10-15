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

// Why the last check refused a form, for Server health: kept until a check passes.
public enum TurnstileTrouble
{
    Hostname,
    Action,
    Secret,
    Refused,
    Unreachable,
}

public sealed record TurnstileProblem(TurnstileTrouble Trouble, string Detail, DateTimeOffset At);

public sealed class Turnstile(HttpClient http, IConfiguration configuration, ILogger<Turnstile> logger) : ITurnstile
{
    private const string Siteverify = "https://challenges.cloudflare.com/turnstile/v0/siteverify";
    private const int MaxTokenLength = 2048;

    public string? SiteKey => configuration["Turnstile:SiteKey"] is { Length: > 0 } key ? key.Trim() : null;

    private string? Secret => configuration["Turnstile:SecretKey"] is { Length: > 0 } secret ? secret.Trim() : null;

    // The last refusal that was not the visitor's own doing, until a check passes; one for the whole shelf.
    public static TurnstileProblem? Problem { get; private set; }

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
                return Refuse(TurnstileTrouble.Unreachable, $"HTTP {(int)response.StatusCode}");
            }

            var answer = await response.Content.ReadFromJsonAsync<Answer>(timeout.Token);
            if (answer is not { Success: true })
            {
                var codes = answer?.ErrorCodes is { Length: > 0 } given ? given : ["no answer"];
                return Refuse(codes.Contains("invalid-input-secret") ? TurnstileTrouble.Secret : TurnstileTrouble.Refused, string.Join(", ", codes));
            }

            if (!string.Equals(answer.Action, action, StringComparison.Ordinal))
            {
                return Refuse(TurnstileTrouble.Action, $"{answer.Action ?? "none"} ≠ {action}");
            }

            if (answer.Hostname is not { } hostname || !hostnames.Contains(hostname.ToLowerInvariant()))
            {
                return Refuse(TurnstileTrouble.Hostname, answer.Hostname ?? "none");
            }

            Problem = null;
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // Out of reach, too slow, or an answer that is not Turnstile's: no.
            logger.LogWarning(ex, "Turnstile's check did not finish; the form was refused.");
            Problem = new TurnstileProblem(TurnstileTrouble.Unreachable, ex.Message, DateTimeOffset.UtcNow);
            return false;
        }

        bool Refuse(TurnstileTrouble trouble, string detail)
        {
            logger.LogWarning("Turnstile refused a {Action} form: {Trouble} ({Detail}); expected hosts {Hostnames}.", action, trouble, detail, string.Join(", ", hostnames));
            Problem = new TurnstileProblem(trouble, detail, DateTimeOffset.UtcNow);
            return false;
        }
    }

    private sealed record Answer(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("action")] string? Action,
        [property: JsonPropertyName("hostname")] string? Hostname,
        [property: JsonPropertyName("error-codes")] string[]? ErrorCodes);
}
