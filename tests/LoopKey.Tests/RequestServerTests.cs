using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;

namespace LoopKey.Tests;

public class RequestServerTests
{
    [Fact]
    public async Task ServesManifestAndTerminalStatus()
    {
        var port = FindFreePort();
        var manifest = new AuthManifest(
            "request-id",
            "corp",
            "https://login.microsoftonline.com/tenant/oauth2/v2.0/authorize?state=state",
            8400,
            DateTimeOffset.UtcNow.AddMinutes(5));
        var state = new RequestState(manifest);
        await using var server = new RequestServer(state, port);
        server.Start();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var receivedManifest = await client.GetFromJsonAsync<AuthManifest>(
            "/v1/requests/request-id",
            JsonDefaults.Options);
        state.SetStatus("succeeded", "Authenticated.");
        var receivedStatus = await client.GetFromJsonAsync<AuthStatus>(
            "/v1/requests/request-id/status",
            JsonDefaults.Options);

        Assert.Equal(manifest, receivedManifest);
        Assert.Equal(new AuthStatus("succeeded", "Authenticated."), receivedStatus);
        await state.StatusObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
