namespace Shelf.Api.Books;

// A stored file is named for good when it is saved and never changed in place, so its SHA-256 can be
// worked out once and kept in a small file beside it. Sync then reads that instead of the whole file.
public static class Fingerprint
{
    public const string Extension = ".sha256";

    public static async Task<string?> ReadOrComputeAsync(string sidecar, Func<Task<string?>> compute, CancellationToken cancellationToken)
    {
        if (File.Exists(sidecar))
        {
            var kept = (await File.ReadAllTextAsync(sidecar, cancellationToken)).Trim();
            if (kept.Length == 64)
            {
                return kept;
            }
        }

        var hash = await compute();
        if (hash is not null)
        {
            await WriteAsync(sidecar, hash, cancellationToken);
        }

        return hash;
    }

    public static async Task WriteAsync(string sidecar, string hash, CancellationToken cancellationToken)
    {
        var temp = sidecar + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temp, hash, cancellationToken);
            File.Move(temp, sidecar, overwrite: true);
        }
        catch (IOException)
        {
            // Another request wrote it first; either copy is the same.
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public static void Forget(string sidecar)
    {
        try
        {
            File.Delete(sidecar);
        }
        catch (IOException)
        {
        }
    }
}
