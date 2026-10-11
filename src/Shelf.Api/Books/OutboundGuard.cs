using System.Net;
using System.Net.Sockets;

namespace Shelf.Api.Books;

// What a request to an address a reader typed may reach. Checked at the moment of connecting, on the address the name
// resolved to, so a name that resolves somewhere else later, or a redirect, is held to the same rule.
public enum OutboundReach
{
    // The open internet only: never this machine, the local network, or a cloud's metadata service. For addresses
    // any reader can give whose answers come back to them (a podcast's feed and its episodes, an Audiobookshelf to
    // import from), so the shelf can't be used to look inside the network it runs in.
    Public,

    // The internet and the local network, as a notice to ntfy or a webhook to Home Assistant on the same network
    // needs; never link-local addresses, where clouds keep their metadata service.
    Local,
}

// A connection the guard refused: not a passing fault, so never tried again, and no sign the other end is down.
public sealed class OutboundRefused(string host) : Exception($"Shelf does not reach {host}: it is on this machine or its network.");

public static class OutboundGuard
{
    // Whether a failed request was the guard's refusal, however the client wrapped it.
    public static bool IsRefusal(Exception? exception)
    {
        for (var inner = exception; inner is not null; inner = inner.InnerException)
        {
            if (inner is OutboundRefused)
            {
                return true;
            }
        }

        return false;
    }

    public static bool Allows(IPAddress address, OutboundReach reach)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            // Nowhere, link-local (169.254, the metadata services'), multicast, and reserved: never.
            if (b[0] == 0 || (b[0] == 169 && b[1] == 254) || b[0] >= 224)
            {
                return false;
            }

            var local = b[0] == 127 || b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
            return reach == OutboundReach.Local || !local;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6None) || address.IsIPv6LinkLocal || address.IsIPv6Multicast)
            {
                return false;
            }

            var b = address.GetAddressBytes();
            var local = IPAddress.IsLoopback(address) || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal || (b[0] & 0xfe) == 0xfc;
            return reach == OutboundReach.Local || !local;
        }

        return false;
    }

    // A handler that connects only where the reach allows: every address the name resolves to must be allowed.
    public static SocketsHttpHandler Handler(OutboundReach reach) => new()
    {
        ConnectCallback = async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, cancellationToken);
            if (addresses.Length == 0 || addresses.Any(address => !Allows(address, reach)))
            {
                throw new OutboundRefused(host);
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    // How far addresses readers give may reach: the open internet, unless an admin lets them reach the local network.
    public static OutboundReach ReaderReach(IConfiguration configuration) =>
        configuration.GetValue("Network:AllowPrivateAddresses", false) ? OutboundReach.Local : OutboundReach.Public;
}
