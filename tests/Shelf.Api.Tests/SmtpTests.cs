using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shelf.Api.Readers;

namespace Shelf.Api.Tests;

public sealed class SmtpTests
{
    [Fact]
    public async Task Email_goes_out_through_a_real_smtp_conversation()
    {
        await using var server = new LoopbackSmtpServer();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:Host"] = "127.0.0.1",
                ["Email:Port"] = server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Email:Security"] = "none",
                ["Email:User"] = "shelf",
                ["Email:Password"] = "secret",
                ["Email:From"] = "shelf@example.org",
            })
            .Build();
        var sender = new SmtpEmailSender(configuration, NullLogger<SmtpEmailSender>.Instance);
        Assert.True(sender.Enabled);

        await sender.SendAsync(new EmailMessage("reader@example.org", "Your loans on Shelf", "Hello Ged,\n\nA book is overdue."), CancellationToken.None);

        var mail = await server.Received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("MAIL FROM:<shelf@example.org>", mail.Envelope);
        Assert.Contains("RCPT TO:<reader@example.org>", mail.Envelope);
        Assert.Contains("AUTH PLAIN", mail.Envelope);
        Assert.Contains("Subject: Your loans on Shelf", mail.Data);
        Assert.Contains("A book is overdue.", mail.Data);
        Assert.False(new SmtpEmailSender(new ConfigurationBuilder().Build(), NullLogger<SmtpEmailSender>.Instance).Enabled);
    }

    [Fact]
    public async Task A_file_goes_with_an_email_as_an_attachment()
    {
        await using var server = new LoopbackSmtpServer();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:Host"] = "127.0.0.1",
                ["Email:Port"] = server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Email:Security"] = "none",
                ["Email:From"] = "shelf@example.org",
            })
            .Build();
        var file = Path.Combine(Path.GetTempPath(), $"attached-{Guid.NewGuid():N}.epub");
        await File.WriteAllBytesAsync(file, Encoding.ASCII.GetBytes("pretend this is an EPUB"));
        try
        {
            await new SmtpEmailSender(configuration, NullLogger<SmtpEmailSender>.Instance).SendAsync(
                new EmailMessage("someone@kindle.com", "The Dispossessed", "Sent from Shelf.", [new EmailAttachment("Le Guin - The Dispossessed.epub", "application/epub+zip", file)]),
                CancellationToken.None);
            var mail = await server.Received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains("multipart/mixed", mail.Data);
            Assert.Contains("Content-Type: application/epub+zip", mail.Data);
            Assert.Contains("Content-Disposition: attachment; filename=\"Le Guin - The Dispossessed.epub\"", mail.Data);
            Assert.Contains(Convert.ToBase64String(Encoding.ASCII.GetBytes("pretend this is an EPUB")), mail.Data);
            Assert.Contains("Sent from Shelf.", mail.Data);
        }
        finally
        {
            File.Delete(file);
        }
    }

    // Just enough SMTP to take one message: no TLS, and any sign-in is accepted.
    private sealed class LoopbackSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _serving;

        public LoopbackSmtpServer()
        {
            _listener.Start();
            _serving = ServeAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public TaskCompletionSource<(string Envelope, string Data)> Received { get; } = new();

        private async Task ServeAsync()
        {
            using var client = await _listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            var envelope = new StringBuilder();
            await writer.WriteLineAsync("220 localhost ESMTP ready");
            while (await reader.ReadLineAsync() is { } line)
            {
                envelope.AppendLine(line);
                var verb = line.Split(' ')[0].ToUpperInvariant();
                switch (verb)
                {
                    case "EHLO":
                        await writer.WriteLineAsync("250-localhost");
                        await writer.WriteLineAsync("250-AUTH PLAIN LOGIN");
                        await writer.WriteLineAsync("250 8BITMIME");
                        break;
                    case "AUTH":
                        await writer.WriteLineAsync("235 2.7.0 Signed in");
                        break;
                    case "DATA":
                        await writer.WriteLineAsync("354 Go ahead");
                        var data = new StringBuilder();
                        while (await reader.ReadLineAsync() is { } body && body != ".")
                        {
                            data.AppendLine(body);
                        }

                        await writer.WriteLineAsync("250 2.0.0 Taken");
                        Received.TrySetResult((envelope.ToString(), data.ToString()));
                        break;
                    case "QUIT":
                        await writer.WriteLineAsync("221 Bye");
                        return;
                    default:
                        await writer.WriteLineAsync("250 OK");
                        break;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await _serving.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or TimeoutException or IOException)
            {
            }
        }
    }

    [Fact]
    public async Task An_email_that_does_not_go_is_kept_as_the_problem_until_one_does()
    {
        // A port nothing listens on.
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:Host"] = "127.0.0.1",
                ["Email:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Email:Security"] = "none",
                ["Email:From"] = "Shelf <shelf@example.org>",
            })
            .Build();
        var sender = new SmtpEmailSender(configuration, NullLogger<SmtpEmailSender>.Instance);
        await Assert.ThrowsAnyAsync<Exception>(() => sender.SendAsync(new EmailMessage("reader@example.org", "Hello", "Hello."), CancellationToken.None));
        Assert.NotNull(sender.Problem);

        await using var server = new LoopbackSmtpServer();
        var working = new SmtpEmailSender(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:Host"] = "127.0.0.1",
            ["Email:Port"] = server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Email:Security"] = "none",
            ["Email:From"] = "Shelf <shelf@example.org>",
        }).Build(), NullLogger<SmtpEmailSender>.Instance);
        await working.SendAsync(new EmailMessage("reader@example.org", "Hello", "Hello."), CancellationToken.None);
        Assert.Contains("MAIL FROM:<shelf@example.org>", (await server.Received.Task.WaitAsync(TimeSpan.FromSeconds(10))).Envelope);
        Assert.Null(working.Problem);
    }

    [Theory]
    [InlineData("shelf@example.org", "Shelf", "shelf@example.org")]
    [InlineData("Shelf <shelf@example.org>", "Shelf", "shelf@example.org")]
    [InlineData("\"Our Books\" <books@example.org>", "Our Books", "books@example.org")]
    [InlineData(" spaced@example.org ", "Shelf", "spaced@example.org")]
    public void Email_from_may_be_a_bare_address_or_carry_a_name(string from, string name, string address)
    {
        var sender = Shelf.Api.Readers.SmtpEmailSender.Sender(from);
        Assert.Equal((name, address), (sender.Name, sender.Address));
    }

}
