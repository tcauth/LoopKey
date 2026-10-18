using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace LoopKey;

internal static class RequestCommand
{
    public static async Task<int> RunAsync(RequestOptions options, CancellationToken cancellationToken)
    {
        EnsureWindows();
        EnsureFileExists(options.AzdPath, "azd wrapper");
        EnsurePortAvailable(options.CallbackPort);
        EnsurePortAvailable(options.MetadataPort);

        var requestId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var tunnelId = options.TunnelId ?? GenerateTunnelId(requestId);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(options.ExpiresMinutes);
        var requestDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LoopKey",
            "requests",
            requestId);
        Directory.CreateDirectory(requestDirectory);

        var capturePath = Path.Combine(requestDirectory, "authorization-url.txt");
        var browserShim = Path.Combine(requestDirectory, "capture-browser.cmd");
        var executable = Environment.ProcessPath
            ?? throw new CliException("Unable to resolve the LoopKey executable path.");
        await File.WriteAllTextAsync(
            browserShim,
            $"@echo off\r\n\"{executable}\" __capture\r\nexit 0\r\n",
            cancellationToken);

        ManagedProcess? tunnelHost = null;
        ManagedProcess? azdLogin = null;
        RequestServer? server = null;
        var tunnelCreated = false;

        using var expires = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        expires.CancelAfter(TimeSpan.FromMinutes(options.ExpiresMinutes));

        try
        {
            await RunRequiredAsync(
                "devtunnel",
                ["create", tunnelId, "--expiration", "1h", "-d", $"LoopKey {options.Profile} auth request"],
                expires.Token);
            tunnelCreated = true;
            await RunRequiredAsync(
                "devtunnel",
                ["port", "create", tunnelId, "-p", options.CallbackPort.ToString(), "--protocol", "http"],
                expires.Token);
            await RunRequiredAsync(
                "devtunnel",
                ["port", "create", tunnelId, "-p", options.MetadataPort.ToString(), "--protocol", "http"],
                expires.Token);

            tunnelHost = ProcessRunner.Start("devtunnel", ["host", tunnelId]);
            tunnelHost.Start();
            await tunnelHost.WaitForOutputAsync(
                output => output.Contains("Ready to accept connections", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(30),
                expires.Token);

            var environment = new Dictionary<string, string?>
            {
                ["BROWSER"] = browserShim,
                ["AZD_NON_INTERACTIVE"] = "false",
                [BrowserCapture.CapturePathVariable] = capturePath,
            };
            azdLogin = StartAzd(
                options.AzdPath,
                [
                    "auth",
                    "login",
                    "--tenant-id",
                    options.TenantId,
                    "--redirect-port",
                    options.CallbackPort.ToString(),
                ],
                environment);

            var authorizationUrl = await WaitForCaptureAsync(
                capturePath,
                azdLogin,
                TimeSpan.FromSeconds(30),
                expires.Token);
            BrowserCapture.ValidateAuthorizationUrl(authorizationUrl, options.CallbackPort);

            var manifest = new AuthManifest(
                requestId,
                options.Profile,
                authorizationUrl,
                options.CallbackPort,
                expiresAt);
            var state = new RequestState(manifest);
            server = new RequestServer(state, options.MetadataPort);
            server.Start();

            Console.WriteLine("Authentication request ready.");
            Console.WriteLine();
            Console.WriteLine("Run this once on the laptop:");
            Console.WriteLine(
                $"  loopkey connect {tunnelId} {requestId} --metadata-port {options.MetadataPort}");
            Console.WriteLine();
            Console.WriteLine($"Expires: {expiresAt:O}");
            Console.WriteLine("Waiting for browser authentication...");

            var exitCode = await azdLogin.WaitForExitAsync(expires.Token);
            if (exitCode != 0)
            {
                state.SetStatus("failed", $"azd login failed with exit code {exitCode}.");
                await WaitForStatusObservationAsync(state, cancellationToken);
                throw new CliException($"azd login failed.\n{azdLogin.Output.Trim()}");
            }

            var check = await RunAzdAsync(
                options.AzdPath,
                ["auth", "login", "--check-status", "--tenant-id", options.TenantId],
                environment: null,
                cancellationToken);
            if (check.ExitCode != 0
                || !check.CombinedOutput.Contains("Logged in to Azure as", StringComparison.OrdinalIgnoreCase))
            {
                state.SetStatus("failed", "azd completed but the profile status check failed.");
                await WaitForStatusObservationAsync(state, cancellationToken);
                throw new CliException($"azd profile verification failed.\n{check.CombinedOutput}");
            }

            state.SetStatus("succeeded", $"Authenticated {options.Profile} profile on the DevBox.");
            await WaitForStatusObservationAsync(state, cancellationToken);
            Console.WriteLine($"Authenticated {options.Profile} profile successfully.");
            return 0;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CliException("The authentication request expired.");
        }
        finally
        {
            if (server is not null)
            {
                await server.DisposeAsync();
            }

            if (azdLogin is not null)
            {
                await azdLogin.DisposeAsync();
            }

            if (tunnelHost is not null)
            {
                await tunnelHost.DisposeAsync();
            }

            if (tunnelCreated)
            {
                await ProcessRunner.RunAsync(
                    "devtunnel",
                    ["delete", tunnelId, "--force"],
                    environment: null,
                    CancellationToken.None);
            }

            Directory.Delete(requestDirectory, recursive: true);
        }
    }

    private static ManagedProcess StartAzd(
        string azdPath,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment)
    {
        var process = ProcessRunner.StartRaw(
            "cmd.exe",
            BuildCmdArguments(azdPath, arguments),
            environment);
        process.Start();
        return process;
    }

    private static Task<ProcessResult> RunAzdAsync(
        string azdPath,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken) =>
        ProcessRunner.RunRawAsync(
            "cmd.exe",
            BuildCmdArguments(azdPath, arguments),
            environment,
            cancellationToken);

    internal static string BuildCmdArguments(string executable, IEnumerable<string> arguments) =>
        $"/d /s /c \"\"{executable}\" {string.Join(' ', arguments.Select(QuoteCmdArgument))}\"";

    private static string QuoteCmdArgument(string argument) =>
        argument.Any(char.IsWhiteSpace) ? $"\"{argument.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : argument;

    private static async Task<string> WaitForCaptureAsync(
        string path,
        ManagedProcess azdLogin,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        while (!timeoutSource.IsCancellationRequested)
        {
            if (File.Exists(path))
            {
                var value = await File.ReadAllTextAsync(path, timeoutSource.Token);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            if (azdLogin.HasExited)
            {
                throw new CliException($"azd exited before producing an authorization URL.\n{azdLogin.Output.Trim()}");
            }

            await Task.Delay(100, timeoutSource.Token);
        }

        throw new CliException("azd did not produce an authorization URL before the timeout.");
    }

    private static async Task RunRequiredAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(fileName, arguments, environment: null, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new CliException($"{fileName} failed.\n{result.CombinedOutput}");
        }
    }

    private static async Task WaitForStatusObservationAsync(
        RequestState state,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await state.StatusObserved.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static string GenerateTunnelId(string requestId)
    {
        var machine = new string(
            Environment.MachineName
                .ToLowerInvariant()
                .Where(character => char.IsAsciiLetterOrDigit(character) || character == '-')
                .ToArray());
        return $"loopkey-{machine}-{requestId[..8]}";
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new CliException("The initial LoopKey implementation supports Windows only.");
        }
    }

    private static void EnsureFileExists(string path, string description)
    {
        if (!File.Exists(path))
        {
            throw new CliException($"{description} not found: {path}");
        }
    }

    private static void EnsurePortAvailable(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
        }
        catch (SocketException)
        {
            throw new CliException($"Local port {port} is already in use.");
        }
    }
}
