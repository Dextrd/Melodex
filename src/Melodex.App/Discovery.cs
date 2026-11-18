using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Melodex.Client;

namespace Melodex.App;

/// <summary>A Melodex server found on the network.</summary>
public sealed record FoundServer(Uri Address, string Version);

/// <summary>
/// Looks for Melodex servers on the local network: each address around this device's, at the ports Melodex is usually
/// published on, asked for its version, which any Melodex answers without signing in.
/// </summary>
public sealed class Discovery
{
    // The Compose file's port first, then the usual ones behind a proxy.
    private static readonly int[] Ports = [8787, 80, 8080];
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(1);
    private const int AtOnce = 96;

    private readonly HttpClient _http = new(new HttpClientHandler { UseCookies = false }) { Timeout = Patience };

    public async IAsyncEnumerable<FoundServer> SearchAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var found = Channel.CreateUnbounded<FoundServer>();
        var addresses = (from port in Ports from host in Neighbours() select new Uri($"http://{host}:{port}/")).ToList();
        var search = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(addresses, new ParallelOptions { MaxDegreeOfParallelism = AtOnce, CancellationToken = ct }, async (address, token) =>
                {
                    if (await ProbeAsync(address, token) is { } server)
                        await found.Writer.WriteAsync(server, token);
                });
            }
            finally
            {
                found.Writer.TryComplete();
            }
        }, ct);

        await foreach (var server in found.Reader.ReadAllAsync(ct))
            yield return server;
        await search;
    }

    /// <summary>Whether a Melodex answers at this address, and with which version.</summary>
    public async Task<FoundServer?> ProbeAsync(Uri address, CancellationToken ct)
    {
        try
        {
            var version = await _http.GetFromJsonAsync<ServerVersion>(new Uri(address, "api/v1/version"), MelodexJson.Options, ct);
            return version is { ApiVersion: "v1" } && version.Contracts.ContainsKey("retrieval") ? new FoundServer(address, version.Version) : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
        {
            return null;
        }
    }

    // Every other address in the /24 around each of this device's own on the local network.
    private static IEnumerable<IPAddress> Neighbours()
    {
        var seen = new HashSet<IPAddress>();
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (var unicast in network.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || !IsPrivate(unicast.Address))
                    continue;
                var bytes = unicast.Address.GetAddressBytes();
                for (var last = 1; last < 255; last++)
                {
                    if (last == bytes[3])
                        continue;
                    var neighbour = new IPAddress([bytes[0], bytes[1], bytes[2], (byte)last]);
                    if (seen.Add(neighbour))
                        yield return neighbour;
                }
            }
        }
    }

    // Home networks: 10/8, 172.16/12 and 192.168/16. Not a mobile network's addresses.
    private static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168;
    }
}
