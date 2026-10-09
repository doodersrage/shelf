using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Shelf.Api.Readers;

// Interactive components run outside the HTTP request, so the circuit hands its user to ShelfReader.
public sealed class ReaderCircuitHandler(AuthenticationStateProvider authentication, ShelfReader reader) : CircuitHandler, IDisposable
{
    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        authentication.AuthenticationStateChanged += Changed;
        reader.Use((await authentication.GetAuthenticationStateAsync()).User);
    }

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next) =>
        async context =>
        {
            reader.Use((await authentication.GetAuthenticationStateAsync()).User);
            await next(context);
        };

    public void Dispose() => authentication.AuthenticationStateChanged -= Changed;

    private void Changed(Task<AuthenticationState> state) =>
        _ = state.ContinueWith(
            done => reader.Use(done.Result.User),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
}
