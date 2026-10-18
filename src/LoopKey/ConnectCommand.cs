using System.Diagnostics;
using System.Net.Http.Json;

namespace LoopKey;

internal static class ConnectCommand
{
    public static async Task<int> RunAsync(ConnectOptions options, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new CliException("The initial LoopKey implementation supports Windows only.");
        }

        await using var tunnel = ProcessRunner.Start("devtunnel", ["connect", options.TunnelId]);
        tunnel.Start();

        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{options.MetadataPort}"),
            Timeout = TimeSpan.FromSeconds(3),
        };

        var manifest = await WaitForManifestAsync(client, options, tunnel, cancellationToken);
        if (manifest.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new CliException("The authentication request has expired.");
        }

        if (!options.NoBrowser)
        {
            Process.Start(new ProcessStartInfo(manifest.AuthorizationUrl)
            {
                UseShellExecute = true,
            });
            Console.WriteLine($"Opened the {manifest.Profile} authentication page in the default browser.");
        }
        else
        {
            Console.WriteLine("Browser launch suppressed by --no-browser.");
        }

        Console.WriteLine("Waiting for the host to verify authentication...");
        var status = await WaitForStatusAsync(client, options.RequestId, manifest.ExpiresAt, tunnel, cancellationToken);
        Console.WriteLine(status.Message);
        return status.State == "succeeded" ? 0 : 1;
    }

    private static async Task<AuthManifest> WaitForManifestAsync(
        HttpClient client,
        ConnectOptions options,
        ManagedProcess tunnel,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        Exception? lastError = null;

        while (!timeout.IsCancellationRequested)
        {
            if (tunnel.HasExited)
            {
                throw new CliException($"Dev Tunnel client exited before connecting.\n{tunnel.Output.Trim()}");
            }

            try
            {
                var manifest = await client.GetFromJsonAsync<AuthManifest>(
                    $"/v1/requests/{options.RequestId}",
                    JsonDefaults.Options,
                    timeout.Token);
                if (manifest is not null && manifest.RequestId == options.RequestId)
                {
                    return manifest;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                lastError = exception;
            }

            await Task.Delay(250, timeout.Token);
        }

        throw new CliException(
            $"The private request endpoint did not become ready. {lastError?.Message}".Trim());
    }

    private static async Task<AuthStatus> WaitForStatusAsync(
        HttpClient client,
        string requestId,
        DateTimeOffset expiresAt,
        ManagedProcess tunnel,
        CancellationToken cancellationToken)
    {
        while (DateTimeOffset.UtcNow < expiresAt)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tunnel.HasExited)
            {
                throw new CliException($"Dev Tunnel client disconnected.\n{tunnel.Output.Trim()}");
            }

            try
            {
                var status = await client.GetFromJsonAsync<AuthStatus>(
                    $"/v1/requests/{requestId}/status",
                    JsonDefaults.Options,
                    cancellationToken);
                if (status?.IsTerminal == true)
                {
                    return status;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(1000, cancellationToken);
        }

        return new AuthStatus("expired", "The authentication request expired.");
    }
}
