namespace Shelf.Api.Books;

// Cover pictures kept on the shelf: one a reader uploaded, art found inside an audiobook, or one a catalog sent.
// Each is checked to be a picture by its first bytes, whatever it claims to be, and named for good when saved.
public sealed class CoverStore(IWebHostEnvironment environment, IConfiguration configuration)
{
    public const int MaxBytes = 10 * 1024 * 1024;

    public string Root { get; } = configuration["CoverStore:Root"]
        ?? Path.Combine(environment.ContentRootPath, "covers");

    // JPEG, PNG, GIF, or WebP, told by the bytes they start with.
    public static string? Kind(ReadOnlySpan<byte> bytes) => bytes switch
    {
        [0xFF, 0xD8, 0xFF, ..] => ".jpg",
        [0x89, (byte)'P', (byte)'N', (byte)'G', ..] => ".png",
        [(byte)'G', (byte)'I', (byte)'F', (byte)'8', ..] => ".gif",
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] => ".webp",
        _ => null,
    };

    public async Task<string?> SaveAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        if (bytes.Length is 0 or > MaxBytes || Kind(bytes) is not { } extension)
        {
            return null;
        }

        Directory.CreateDirectory(Root);
        var storedName = $"{Guid.NewGuid():N}{extension}";
        await File.WriteAllBytesAsync(Path.Combine(Root, storedName), bytes, cancellationToken);
        return storedName;
    }

    public async Task<string?> SaveAsync(Stream source, CancellationToken cancellationToken)
    {
        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (copy.Length + read > MaxBytes)
            {
                return null;
            }

            copy.Write(buffer, 0, read);
        }

        return await SaveAsync(copy.ToArray(), cancellationToken);
    }

    public string? OpenPath(string? storedName)
    {
        if (string.IsNullOrWhiteSpace(storedName) || Path.GetFileName(storedName) != storedName || storedName.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var path = Path.Combine(Root, storedName);
        return File.Exists(path) ? path : null;
    }

    public void Delete(string? storedName)
    {
        if (OpenPath(storedName) is { } path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    public static string ContentType(string storedName) => Path.GetExtension(storedName) switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };
}
