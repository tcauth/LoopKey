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
        AuthConfiguration.ValidateCommandAvailability(options.Profile);
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
        ManagedProcess? loginProcess = null;
        RequestServer? server = null;
        var tunnelCreated = false;

        using var expires = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        expires.CancelAfter(TimeSpan.FromMinutes(options.ExpiresMinutes));

        try
        {
            await RunRequiredAsync(
                "devtunnel",
                [
                    "create", tunnelId,
                    "--expiration", "1h",
                    "-d", $"LoopKey {options.Profile.Name} auth request",
                ],
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

            var environment = new Dictionary<string, string?>(
                options.Profile.Login.Environment,
                StringComparer.OrdinalIgnoreCase)
            {
                ["BROWSER"] = browserShim,
                [BrowserCapture.CapturePathVariable] = capturePath,
                [BrowserCapture.AllowedHostsVariable] =
                    string.Join(';', options.Profile.Authorization.AllowedHosts),
            };
            var login = options.Profile.Login.WithCallbackPort(options.CallbackPort);
            loginProcess = StartConfiguredCommand(login, environment);

            var authorizationUrl = await WaitForCaptureAsync(
                capturePath,
                loginProcess,
                TimeSpan.FromSeconds(30),
                expires.Token);
            BrowserCapture.ValidateAuthorizationUrl(
                authorizationUrl,
                options.CallbackPort,
                options.Profile.Authorization);

            var manifest = new AuthManifest(
                requestId,
                options.Profile.Name,
                authorizationUrl,
                options.CallbackPort,
                expiresAt);
            var state = new RequestState(manifest);
            server = new RequestServer(state, options.MetadataPort);
            server.Start();

            Console.WriteLine("Authentication request ready.");
            Console.WriteLine();
            Console.WriteLine("Run this once on the client:");
            Console.WriteLine(
                $"  loopkey connect {tunnelId} {requestId} --metadata-port {options.MetadataPort}");
            Console.WriteLine();
            Console.WriteLine($"Expires: {expiresAt:O}");
            Console.WriteLine("Waiting for browser authentication...");

            var exitCode = await loginProcess.WaitForExitAsync(expires.Token);
            if (exitCode != 0)
            {
                state.SetStatus("failed", $"The login command failed with exit code {exitCode}.");
                await WaitForStatusObservationAsync(state, cancellationToken);
                throw new CliException(
                    $"Configured login command failed.\n{loginProcess.Output.Trim()}");
            }

            var check = await RunConfiguredCommandAsync(
                options.Profile.Verification,
                environmentOverrides: null,
                cancellationToken);
            if (check.ExitCode != 0
                || !check.CombinedOutput.Contains(
                    options.Profile.SuccessOutputContains,
                    StringComparison.Ordinal))
            {
                state.SetStatus("failed", "The configured authentication verification failed.");
                await WaitForStatusObservationAsync(state, cancellationToken);
                throw new CliException(
                    $"Configured authentication verification failed.\n{check.CombinedOutput}");
            }

            if (options.Profile.PostAuth is not null)
            {
                var postAuth = await RunConfiguredCommandAsync(
                    options.Profile.PostAuth,
                    environmentOverrides: null,
                    cancellationToken);
                if (postAuth.ExitCode != 0)
                {
                    state.SetStatus(
                        "failed",
                        "Authentication succeeded, but the configured post-auth command failed.");
                    await WaitForStatusObservationAsync(state, cancellationToken);
                    throw new CliException(
                        $"Post-auth command failed with exit code {postAuth.ExitCode}.\n"
                        + postAuth.CombinedOutput);
                }

                Console.WriteLine("Post-auth command completed successfully.");
            }

            state.SetStatus(
                "succeeded",
                $"Authenticated profile '{options.Profile.Name}' on the host.");
            await WaitForStatusObservationAsync(state, cancellationToken);
            Console.WriteLine($"Authenticated profile '{options.Profile.Name}' successfully.");
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

            if (loginProcess is not null)
            {
                await loginProcess.DisposeAsync();
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

    private static ManagedProcess StartConfiguredCommand(
        ConfiguredCommand command,
        IReadOnlyDictionary<string, string?>? environmentOverrides)
    {
        var environment = MergeEnvironment(command.Environment, environmentOverrides);
        var process = IsCommandScript(command.FileName)
            ? ProcessRunner.StartRaw(
                "cmd.exe",
                BuildCmdArguments(command.FileName, command.Arguments),
                environment)
            : ProcessRunner.Start(command.FileName, command.Arguments, environment);
        process.Start();
        return process;
    }

    internal static Task<ProcessResult> RunConfiguredCommandAsync(
        ConfiguredCommand command,
        IReadOnlyDictionary<string, string?>? environmentOverrides,
        CancellationToken cancellationToken)
    {
        var environment = MergeEnvironment(command.Environment, environmentOverrides);
        return IsCommandScript(command.FileName)
            ? ProcessRunner.RunRawAsync(
                "cmd.exe",
                BuildCmdArguments(command.FileName, command.Arguments),
                environment,
                cancellationToken)
            : ProcessRunner.RunAsync(
                command.FileName,
                command.Arguments,
                environment,
                cancellationToken);
    }

    internal static string BuildCmdArguments(string executable, IEnumerable<string> arguments) =>
        $"/d /s /c \"\"{executable}\" {string.Join(' ', arguments.Select(QuoteCmdArgument))}\"";

    private static string QuoteCmdArgument(string argument) =>
        argument.Any(char.IsWhiteSpace) ? $"\"{argument.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : argument;

    private static async Task<string> WaitForCaptureAsync(
        string path,
        ManagedProcess loginProcess,
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

            if (loginProcess.HasExited)
            {
                throw new CliException(
                    "The login command exited before producing an authorization URL.\n"
                    + loginProcess.Output.Trim());
            }

            await Task.Delay(100, timeoutSource.Token);
        }

        throw new CliException(
            "The login command did not produce an authorization URL before the timeout.");
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

    private static IReadOnlyDictionary<string, string?> MergeEnvironment(
        IReadOnlyDictionary<string, string?> configured,
        IReadOnlyDictionary<string, string?>? overrides)
    {
        var environment = new Dictionary<string, string?>(
            configured,
            StringComparer.OrdinalIgnoreCase);
        if (overrides is not null)
        {
            foreach (var pair in overrides)
            {
                environment[pair.Key] = pair.Value;
            }
        }

        return environment;
    }

    private static bool IsCommandScript(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".cmd" or ".bat";

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
