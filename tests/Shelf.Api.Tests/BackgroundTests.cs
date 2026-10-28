using System.Net;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Shelf.Api.Tests;

public sealed class BackgroundTests(ShelfApiFactory factory) : IClassFixture<ShelfApiFactory>
{
    // A background job failing in a way it didn't expect stops that job, never the server: the shelf keeps answering.
    [Fact]
    public async Task A_background_job_that_fails_unexpectedly_stops_itself_not_the_server()
    {
        var failing = new FailingJob();
        await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IHostedService>(failing)));
        var client = app.CreateClient();

        await failing.Failed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // Long enough for the host to act on the failure, as it would by stopping.
        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.False(app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/alive")).StatusCode);
    }

    private sealed class FailingJob : BackgroundService
    {
        public TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();
            Failed.TrySetResult();
            throw new InvalidCastException("Nothing expected this.");
        }
    }
}
