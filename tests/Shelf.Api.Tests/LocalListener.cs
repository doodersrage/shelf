using System.Net;
using System.Net.Sockets;

namespace Shelf.Api.Tests;

// A web server on this machine for a test to point the shelf at. Its port is one the system says is free, where a
// port picked at random could already be taken; another test can still take it in the moment before the listener
// does, so a taken one is given up for another.
internal static class LocalListener
{
    public static HttpListener Start(out string address)
    {
        for (var attempt = 1; ; attempt++)
        {
            var listener = new HttpListener();
            address = $"http://127.0.0.1:{FreePort()}/";
            listener.Prefixes.Add(address);
            try
            {
                listener.Start();
                return listener;
            }
            catch (HttpListenerException) when (attempt < 10)
            {
                listener.Close();
            }
        }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
