using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace LoopKey;

internal sealed class RequestState(AuthManifest manifest)
{
    private readonly object gate = new();
    private AuthStatus status = new("waiting", "Waiting for browser authentication.");

    public AuthManifest Manifest { get; } = manifest;
    public TaskCompletionSource StatusObserved { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AuthStatus Status
    {
        get
        {
            lock (gate)
            {
                return status;
            }
        }
    }

    public void SetStatus(string state, string message)
    {
        lock (gate)
        {
            status = new AuthStatus(state, message);
        }
    }
}

internal sealed class RequestServer(RequestState state, int port) : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, port);
    private readonly CancellationTokenSource cancellation = new();
    private Task? acceptLoop;

    public void Start()
    {
        listener.Start();
        acceptLoop = AcceptLoopAsync(cancellation.Token);
    }

    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        listener.Stop();
        if (acceptLoop is not null)
        {
            try
            {
                await acceptLoop;
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }
        }

        cancellation.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(cancellationToken);
            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        using var stream = client.GetStream();
        using var reader = new StreamReader(
            stream,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);

        var requestLine = await reader.ReadLineAsync(cancellationToken);
        string? header;
        do
        {
            header = await reader.ReadLineAsync(cancellationToken);
        }
        while (!string.IsNullOrEmpty(header));

        if (requestLine is null)
        {
            return;
        }

        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] != "GET")
        {
            await WriteResponseAsync(stream, 405, new { error = "method_not_allowed" }, cancellationToken);
            return;
        }

        var basePath = $"/v1/requests/{state.Manifest.RequestId}";
        switch (parts[1])
        {
            case var path when path == basePath:
                await WriteResponseAsync(stream, 200, state.Manifest, cancellationToken);
                break;
            case var path when path == $"{basePath}/status":
                var status = state.Status;
                await WriteResponseAsync(stream, 200, status, cancellationToken);
                if (status.IsTerminal)
                {
                    state.StatusObserved.TrySetResult();
                }

                break;
            default:
                await WriteResponseAsync(stream, 404, new { error = "not_found" }, cancellationToken);
                break;
        }
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int statusCode,
        object body,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(body, JsonDefaults.Options);
        var reason = statusCode switch
        {
            200 => "OK",
            404 => "Not Found",
            405 => "Method Not Allowed",
            _ => "Error",
        };
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {reason}\r\n"
            + "Content-Type: application/json; charset=utf-8\r\n"
            + "Cache-Control: no-store\r\n"
            + "Connection: close\r\n"
            + $"Content-Length: {payload.Length}\r\n\r\n");

        await stream.WriteAsync(headers, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
    }
}
