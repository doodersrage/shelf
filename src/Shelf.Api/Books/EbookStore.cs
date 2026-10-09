using System.Security.Cryptography;

namespace Shelf.Api.Books;

public sealed class EbookStore(IWebHostEnvironment environment, IConfiguration configuration)
{
    public const long MaxBytes = 80L * 1024 * 1024;

    public string Root { get; } = configuration["EbookStore:Root"]
        ?? Path.Combine(environment.ContentRootPath, "ebooks");

    public async Task<EbookSave> SaveAsync(Stream source, string originalName, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(originalName).ToLowerInvariant();
        if (extension is not ".epub" and not ".pdf")
        {
            return new EbookSave(EbookSaveStatus.Unsupported, null, null);
        }

        Directory.CreateDirectory(Root);
        var storedName = $"{Guid.NewGuid():N}{extension}";
        var fullPath = Path.Combine(Root, storedName);
        await using var output = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > MaxBytes)
            {
                output.Close();
                File.Delete(fullPath);
                return new EbookSave(EbookSaveStatus.TooLarge, null, null);
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        var displayName = Path.GetFileName(originalName.Trim());
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = storedName;
        }

        if (displayName.Length > 200)
        {
            displayName = displayName[^200..];
        }

        return new EbookSave(EbookSaveStatus.Saved, storedName, displayName);
    }

    public string? OpenPath(string? storedName)
    {
        if (string.IsNullOrWhiteSpace(storedName))
        {
            return null;
        }

        var name = Path.GetFileName(storedName);
        if (!string.Equals(name, storedName, StringComparison.Ordinal) || name.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var root = Path.GetFullPath(Root);
        var full = Path.GetFullPath(Path.Combine(root, name));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal) || !File.Exists(full))
        {
            return null;
        }

        return full;
    }

    public void Delete(string? storedName)
    {
        var full = OpenPath(storedName);
        if (full is null)
        {
            return;
        }

        try
        {
            File.Delete(full);
        }
        catch (IOException)
        {
        }
    }

    public async Task<string?> HashAsync(string? storedName, CancellationToken cancellationToken)
    {
        var path = OpenPath(storedName);
        if (path is null)
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool IsPdf(string? storedName) =>
        storedName?.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) == true;

    public static bool IsEpub(string? storedName) =>
        storedName?.EndsWith(".epub", StringComparison.OrdinalIgnoreCase) == true;
}

public enum EbookSaveStatus
{
    Saved,
    Unsupported,
    TooLarge,
}

public sealed record EbookSave(EbookSaveStatus Status, string? StoredName, string? FileName);
