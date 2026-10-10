using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;

namespace Shelf.Api.Books;

public sealed record OffsiteResult(DateTimeOffset At, string Where, bool Sent, string? Problem);

// A copy of each nightly backup somewhere other than the disk it was made on: a second folder (another disk, or a
// network share mounted on the server), S3-compatible storage (Amazon S3, Backblaze B2, Wasabi, Cloudflare R2,
// MinIO), or both. Each keeps as many as the backups folder does. A copy that fails is reported, and never undoes
// the backup itself.
public sealed class OffsiteBackup(IConfiguration configuration, ILogger<OffsiteBackup> logger)
{
    private readonly List<OffsiteResult> last = [];

    public string? CopyFolder => configuration["Backup:CopyTo"] is { Length: > 0 } folder ? Path.GetFullPath(folder) : null;

    public string? Bucket => configuration["Backup:S3:Bucket"] is { Length: > 0 } bucket
        && configuration["Backup:S3:AccessKey"] is { Length: > 0 } && configuration["Backup:S3:SecretKey"] is { Length: > 0 }
        ? bucket
        : null;

    private string Prefix => (configuration["Backup:S3:Prefix"] ?? "shelf/").TrimStart('/');

    public string? S3Description => Bucket is { } bucket
        ? $"{(configuration["Backup:S3:Endpoint"] is { Length: > 0 } endpoint ? new Uri(endpoint).Host : "s3")}/{bucket}/{Prefix}"
        : null;

    // How the last copies went, newest first.
    public IReadOnlyList<OffsiteResult> Last
    {
        get
        {
            lock (last)
            {
                return [.. last];
            }
        }
    }

    public async Task SendAsync(string path, int keep, CancellationToken cancellationToken)
    {
        var results = new List<OffsiteResult>();
        if (CopyFolder is { } folder)
        {
            results.Add(await TryAsync(folder, () => CopyAsync(path, folder, keep, cancellationToken)));
        }

        if (Bucket is { } bucket)
        {
            results.Add(await TryAsync(S3Description!, () => UploadAsync(path, bucket, keep, cancellationToken)));
        }

        lock (last)
        {
            last.Clear();
            last.AddRange(results);
        }
    }

    private async Task<OffsiteResult> TryAsync(string where, Func<Task> send)
    {
        try
        {
            await send();
            logger.LogInformation("Copied the backup to {Where}.", where);
            return new OffsiteResult(DateTimeOffset.UtcNow, where, true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AmazonServiceException or AmazonClientException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not copy the backup to {Where}.", where);
            return new OffsiteResult(DateTimeOffset.UtcNow, where, false, ex.Message);
        }
    }

    private static async Task CopyAsync(string path, string folder, int keep, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, Path.GetFileName(path));
        var partial = target + ".partial";
        await using (var source = File.OpenRead(path))
        await using (var copy = File.Create(partial))
        {
            await source.CopyToAsync(copy, cancellationToken);
        }

        File.Move(partial, target, overwrite: true);
        foreach (var old in Directory.EnumerateFiles(folder, BackupSchedule.Prefix + "*.zip").OrderDescending(StringComparer.Ordinal).Skip(keep))
        {
            File.Delete(old);
        }
    }

    private async Task UploadAsync(string path, string bucket, int keep, CancellationToken cancellationToken)
    {
        using var client = Client();
        // Larger backups go up in parts (Backup:S3:PartSizeMegabytes, 5 at the least, as S3 asks), so a dropped
        // connection costs one part rather than the whole file.
        using var transfer = new TransferUtility(client, new TransferUtilityConfig
        {
            MinSizeBeforePartUpload = configuration.GetValue("Backup:S3:MultipartAboveBytes", 16L * 1024 * 1024),
        });
        await transfer.UploadAsync(new TransferUtilityUploadRequest
        {
            BucketName = bucket,
            Key = Prefix + Path.GetFileName(path),
            FilePath = path,
            ContentType = "application/zip",
            PartSize = Math.Max(5, configuration.GetValue("Backup:S3:PartSizeMegabytes", 64)) * 1024L * 1024,
        }, cancellationToken);

        // The names carry the time, so the newest sort last; anything past the number kept goes.
        var keys = new List<string>();
        var request = new ListObjectsV2Request { BucketName = bucket, Prefix = Prefix + BackupSchedule.Prefix };
        ListObjectsV2Response page;
        do
        {
            page = await client.ListObjectsV2Async(request, cancellationToken);
            keys.AddRange((page.S3Objects ?? []).Select(item => item.Key).Where(key => key.EndsWith(".zip", StringComparison.Ordinal)));
            request.ContinuationToken = page.NextContinuationToken;
        }
        while (page.IsTruncated == true);

        var stale = keys.OrderDescending(StringComparer.Ordinal).Skip(keep).ToList();
        if (stale.Count > 0)
        {
            await client.DeleteObjectsAsync(new DeleteObjectsRequest
            {
                BucketName = bucket,
                Objects = stale.Select(key => new KeyVersion { Key = key }).ToList(),
            }, cancellationToken);
        }
    }

    private AmazonS3Client Client()
    {
        var region = configuration["Backup:S3:Region"] is { Length: > 0 } named ? named : "us-east-1";
        var config = new AmazonS3Config
        {
            // Several S3-compatible services refuse the checksums the SDK adds by default; send them only when an
            // operation needs one.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        if (configuration["Backup:S3:Endpoint"] is { Length: > 0 } endpoint)
        {
            config.ServiceURL = endpoint;
            config.AuthenticationRegion = region;
            config.ForcePathStyle = configuration.GetValue("Backup:S3:PathStyle", true);
        }
        else
        {
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        }

        return new AmazonS3Client(new BasicAWSCredentials(configuration["Backup:S3:AccessKey"], configuration["Backup:S3:SecretKey"]), config);
    }
}
