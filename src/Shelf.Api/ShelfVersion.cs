using System.Reflection;

namespace Shelf.Api;

// The version from Directory.Build.props, and the commit it was built from when the build knew it.
public static class ShelfVersion
{
    private static readonly string Informational =
        typeof(ShelfVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static string Number => Informational.Split('+')[0];

    public static string? Commit => Informational.Split('+') is [_, var commit, ..] ? commit[..Math.Min(12, commit.Length)] : null;

    public static VersionResponse Response => new(Number, Commit);
}

public sealed record VersionResponse(string Version, string? Commit);
