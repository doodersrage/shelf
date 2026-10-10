using System.Collections.Concurrent;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class OffsiteBackupTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>, IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"shelf-offsite-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Each_backup_is_copied_to_another_folder_and_to_s3_keeping_as_many_as_the_shelf_does()
    {
        await using var s3 = await FakeS3.StartAsync();
        var copies = Path.Combine(root, "second-disk");
        await using var app = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Backup:Folder", Path.Combine(root, "backups"));
            builder.UseSetting("Backup:Keep", "2");
            builder.UseSetting("Backup:CopyTo", copies);
            builder.UseSetting("Backup:S3:Endpoint", s3.Address);
            builder.UseSetting("Backup:S3:Bucket", "books");
            builder.UseSetting("Backup:S3:Prefix", "home/shelf/");
            builder.UseSetting("Backup:S3:AccessKey", "an-access-key");
            builder.UseSetting("Backup:S3:SecretKey", "a-secret-key");
            // Every backup goes up the way a large one does, in parts.
            builder.UseSetting("Backup:S3:MultipartAboveBytes", "1");
        });
        var schedule = app.Services.GetRequiredService<BackupSchedule>();
        var offsite = app.Services.GetRequiredService<OffsiteBackup>();

        var taken = new List<AutomaticBackup>();
        for (var n = 0; n < 3; n++)
        {
            taken.Add(await schedule.TakeAsync(CancellationToken.None));
        }

        var kept = taken.Skip(1).Select(backup => backup.Name).Order().ToList();
        Assert.Equal(kept, Directory.EnumerateFiles(copies).Select(Path.GetFileName).Order());
        Assert.Equal(kept.Select(name => $"home/shelf/{name}"), s3.Objects.Keys.Order());
        var newest = taken[^1];
        Assert.Equal(await File.ReadAllBytesAsync(schedule.PathOf(newest.Name)!), s3.Objects[$"home/shelf/{newest.Name}"]);
        Assert.Equal(await File.ReadAllBytesAsync(schedule.PathOf(newest.Name)!), await File.ReadAllBytesAsync(Path.Combine(copies, newest.Name)));
        Assert.All(offsite.Last, result => Assert.True(result.Sent, result.Problem));
        Assert.Equal(2, offsite.Last.Count);
        Assert.All(s3.Authorizations, value => Assert.StartsWith("AWS4-HMAC-SHA256 Credential=an-access-key/", value));
        Assert.Equal(3, s3.MultipartUploads);
    }

    [Fact]
    public async Task A_copy_that_fails_is_reported_and_the_backup_is_still_made()
    {
        await using var s3 = await FakeS3.StartAsync();
        s3.Refuse = true;
        await using var app = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Backup:Folder", Path.Combine(root, "backups"));
            builder.UseSetting("Backup:S3:Endpoint", s3.Address);
            builder.UseSetting("Backup:S3:Bucket", "books");
            builder.UseSetting("Backup:S3:AccessKey", "an-access-key");
            builder.UseSetting("Backup:S3:SecretKey", "the-wrong-key");
        });
        var backup = await app.Services.GetRequiredService<BackupSchedule>().TakeAsync(CancellationToken.None);
        Assert.NotNull(app.Services.GetRequiredService<BackupSchedule>().PathOf(backup.Name));
        var result = Assert.Single(app.Services.GetRequiredService<OffsiteBackup>().Last);
        Assert.False(result.Sent);
        Assert.Contains("Access Denied", result.Problem);
    }

    // Enough of S3 for the SDK: path-style PUT, list-type=2, and a multi-object delete.
    private sealed class FakeS3 : IAsyncDisposable
    {
        private WebApplication app = default!;

        public ConcurrentDictionary<string, byte[]> Objects { get; } = new();

        public ConcurrentBag<string> Authorizations { get; } = [];

        public bool Refuse { get; set; }

        public int MultipartUploads { get; private set; }

        private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, byte[]>> parts = new();

        public string Address { get; private set; } = "";

        public static async Task<FakeS3> StartAsync()
        {
            var fake = new FakeS3();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            fake.app = builder.Build();
            fake.app.Use(async (context, next) =>
            {
                fake.Authorizations.Add(context.Request.Headers.Authorization.ToString());
                if (fake.Refuse)
                {
                    context.Response.StatusCode = 403;
                    context.Response.ContentType = "application/xml";
                    await context.Response.WriteAsync("<Error><Code>AccessDenied</Code><Message>Access Denied</Message></Error>");
                    return;
                }

                await next();
            });
            fake.app.MapPut("/{bucket}/{**key}", async (string key, HttpRequest request, HttpResponse response) =>
            {
                using var buffer = new MemoryStream();
                await request.Body.CopyToAsync(buffer);
                var streaming = request.Headers["x-amz-content-sha256"].ToString().StartsWith("STREAMING-", StringComparison.Ordinal);
                var bytes = streaming ? Unchunk(buffer.ToArray()) : buffer.ToArray();
                if (request.Query["uploadId"].ToString() is { Length: > 0 } upload)
                {
                    // One part of a multipart upload, kept by its number until the upload completes.
                    fake.parts.GetOrAdd(upload, _ => new())[int.Parse(request.Query["partNumber"]!)] = bytes;
                }
                else
                {
                    fake.Objects[key] = bytes;
                }

                response.Headers.ETag = $"\"{Guid.NewGuid():N}\"";
                return Results.Ok();
            });
            fake.app.MapPost("/{bucket}/{**key}", (string bucket, string key, HttpRequest request) =>
            {
                XNamespace s3 = "http://s3.amazonaws.com/doc/2006-03-01/";
                if (request.Query.ContainsKey("uploads"))
                {
                    var upload = Guid.NewGuid().ToString("N");
                    fake.parts[upload] = new();
                    return Results.Content(new XElement(s3 + "InitiateMultipartUploadResult",
                        new XElement(s3 + "Bucket", bucket), new XElement(s3 + "Key", key), new XElement(s3 + "UploadId", upload)).ToString(), "application/xml");
                }

                var finished = request.Query["uploadId"].ToString();
                fake.parts.TryRemove(finished, out var received);
                fake.Objects[key] = received!.OrderBy(part => part.Key).SelectMany(part => part.Value).ToArray();
                fake.MultipartUploads++;
                return Results.Content(new XElement(s3 + "CompleteMultipartUploadResult",
                    new XElement(s3 + "Bucket", bucket), new XElement(s3 + "Key", key), new XElement(s3 + "ETag", "\"done\"")).ToString(), "application/xml");
            });
            fake.app.MapGet("/{bucket}", (string bucket, string? prefix) =>
            {
                XNamespace s3 = "http://s3.amazonaws.com/doc/2006-03-01/";
                var keys = fake.Objects.Keys.Where(key => prefix is null || key.StartsWith(prefix, StringComparison.Ordinal)).Order().ToList();
                var xml = new XElement(s3 + "ListBucketResult",
                    new XElement(s3 + "Name", bucket),
                    new XElement(s3 + "Prefix", prefix),
                    new XElement(s3 + "KeyCount", keys.Count),
                    new XElement(s3 + "IsTruncated", "false"),
                    keys.Select(key => new XElement(s3 + "Contents", new XElement(s3 + "Key", key), new XElement(s3 + "Size", fake.Objects[key].Length))));
                return Results.Content(xml.ToString(), "application/xml");
            });
            fake.app.MapPost("/{bucket}", async (HttpRequest request) =>
            {
                var document = XDocument.Parse(await new StreamReader(request.Body).ReadToEndAsync());
                var removed = document.Descendants().Where(element => element.Name.LocalName == "Key").Select(element => element.Value).ToList();
                foreach (var key in removed)
                {
                    fake.Objects.TryRemove(key, out _);
                }

                XNamespace s3 = "http://s3.amazonaws.com/doc/2006-03-01/";
                var xml = new XElement(s3 + "DeleteResult", removed.Select(key => new XElement(s3 + "Deleted", new XElement(s3 + "Key", key))));
                return Results.Content(xml.ToString(), "application/xml");
            });
            await fake.app.StartAsync();
            fake.Address = fake.app.Urls.First();
            return fake;
        }

        // aws-chunked: "size;chunk-signature=...\r\n" then that many bytes and "\r\n", ending with a size of 0.
        private static byte[] Unchunk(byte[] body)
        {
            using var output = new MemoryStream();
            var at = 0;
            while (at < body.Length)
            {
                var end = Array.IndexOf(body, (byte)'\n', at);
                var header = Encoding.ASCII.GetString(body, at, end - at).TrimEnd('\r');
                var size = Convert.ToInt32(header.Split(';')[0], 16);
                at = end + 1;
                if (size == 0)
                {
                    break;
                }

                output.Write(body, at, size);
                at += size + 2;
            }

            return output.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
