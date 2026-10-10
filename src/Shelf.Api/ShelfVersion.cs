using System.Reflection;

namespace Shelf.Api;

// The version from Directory.Build.props, and the commit it was built from when the build knew it.
public static class ShelfVersion
{
    private static readonly string Informational =
        typeof(ShelfVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static string Number => Informational.Split('+')[0];

    public static string? Commit => Informational.Split('+') is [_, var commit, ..] ? commit[..Math.Min(12, commit.Length)] : null;

    // Shelf is under the AGPL, so everyone who uses a shelf can get its source. A changed copy sets SourceUrl to its own.
    public const string Source = "https://github.com/doodersrage/shelf";

    public static string SourceUrl(IConfiguration configuration) =>
        configuration["SourceUrl"] is { Length: > 0 } url && Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "https" or "http"
            ? url
            : Source;

    public static VersionResponse Response => new(Number, Commit);
}

public sealed record VersionResponse(string Version, string? Commit);
