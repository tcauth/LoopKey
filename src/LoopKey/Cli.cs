using System.Globalization;
using System.Reflection;

namespace LoopKey;

internal static class Cli
{
    private const string Usage = """
        LoopKey - one-command, tunnel-backed browser authentication for remote DevBoxes

        Usage:
          loopkey request --profile <corp|green> [options]
          loopkey connect <tunnel-id> <request-id> [options]
          loopkey --version

        Request options:
          --tenant-id <id>          Entra tenant. Corp defaults to the Microsoft tenant.
          --callback-port <port>    OAuth loopback callback port (default: 8400).
          --metadata-port <port>    Private request metadata port (default: 8401).
          --expires-minutes <n>     Request lifetime (default: 10).
          --tunnel-id <id>          Dev Tunnel ID (default: generated).
          --azd <path>              azd wrapper path (default: profile-specific wrapper).
          --green-post-auth-script <path>
                                    Optional .ps1, .py, .cmd, .bat, or .exe hook.

        Connect options:
          --metadata-port <port>    Private request metadata port (default: 8401).
          --no-browser              Validate the flow without opening a browser.

        Examples:
          loopkey request --profile corp
          loopkey connect loopkey-jbtem-a1b2c3d4 7f3a9c2e --metadata-port 8401
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (Environment.GetEnvironmentVariable(BrowserCapture.CapturePathVariable) is not null)
            {
                return await BrowserCapture.CaptureAsync();
            }

            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                Console.WriteLine(Usage);
                return 0;
            }
            if (args[0] is "-v" or "--version" or "version")
            {
                Console.WriteLine($"loopkey {Version}");
                return 0;
            }

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            return args[0].ToLowerInvariant() switch
            {
                "request" => await RequestCommand.RunAsync(ParseRequestOptions(args[1..]), cancellation.Token),
                "connect" => await ConnectCommand.RunAsync(ParseConnectOptions(args[1..]), cancellation.Token),
                _ => throw new CliException($"Unknown command '{args[0]}'.\n\n{Usage}"),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (CliException exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
    }

    internal static RequestOptions ParseRequestOptions(string[] args)
    {
        var values = ParseNamedArguments(args);
        var profile = GetRequired(values, "--profile").ToLowerInvariant();
        if (profile is not ("corp" or "green"))
        {
            throw new CliException("--profile must be 'corp' or 'green'.");
        }

        var tenantId = GetOptional(values, "--tenant-id")
            ?? (profile == "corp" ? RequestOptions.MicrosoftTenantId : null)
            ?? Environment.GetEnvironmentVariable("LOOPKEY_GREEN_TENANT_ID")
            ?? throw new CliException("--tenant-id is required for the green profile.");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var defaultAzd = Path.Combine(home, ".azd-profiles", $"azd-{profile}.cmd");
        var greenPostAuthScript = GetOptional(values, "--green-post-auth-script")
            ?? Environment.GetEnvironmentVariable("LOOPKEY_GREEN_POST_AUTH_SCRIPT");
        if (profile != "green" && greenPostAuthScript is not null)
        {
            throw new CliException("--green-post-auth-script can be used only with the green profile.");
        }

        return new RequestOptions(
            profile,
            tenantId,
            ParsePort(values, "--callback-port", 8400),
            ParsePort(values, "--metadata-port", 8401),
            ParsePositiveInt(values, "--expires-minutes", 10),
            GetOptional(values, "--tunnel-id"),
            GetOptional(values, "--azd") ?? defaultAzd,
            greenPostAuthScript);
    }

    internal static ConnectOptions ParseConnectOptions(string[] args)
    {
        var positional = new List<string>();
        var named = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var value = args[index];
            if (!value.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(value);
                continue;
            }

            if (value == "--no-browser")
            {
                named[value] = null;
                continue;
            }

            if (++index >= args.Length)
            {
                throw new CliException($"Missing value for {value}.");
            }

            named[value] = args[index];
        }

        if (positional.Count != 2)
        {
            throw new CliException("connect requires <tunnel-id> and <request-id>.");
        }

        return new ConnectOptions(
            positional[0],
            positional[1],
            ParsePort(named, "--metadata-port", 8401),
            named.ContainsKey("--no-browser"));
    }

    private static Dictionary<string, string?> ParseNamedArguments(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var key = args[index];
            if (!key.StartsWith("--", StringComparison.Ordinal))
            {
                throw new CliException($"Unexpected argument '{key}'.");
            }

            if (++index >= args.Length)
            {
                throw new CliException($"Missing value for {key}.");
            }

            values[key] = args[index];
        }

        return values;
    }

    private static string GetRequired(IReadOnlyDictionary<string, string?> values, string key) =>
        GetOptional(values, key) ?? throw new CliException($"{key} is required.");

    private static string? GetOptional(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var value) ? value : null;

    private static int ParsePort(IReadOnlyDictionary<string, string?> values, string key, int defaultValue)
    {
        var value = GetOptional(values, key);
        if (value is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            throw new CliException($"{key} must be a valid TCP port.");
        }

        return port;
    }

    private static int ParsePositiveInt(
        IReadOnlyDictionary<string, string?> values,
        string key,
        int defaultValue)
    {
        var value = GetOptional(values, key);
        if (value is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) || result < 1)
        {
            throw new CliException($"{key} must be a positive integer.");
        }

        return result;
    }

    internal static string Version =>
        typeof(Cli).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0]
        ?? throw new CliException("Unable to determine the LoopKey version.");
}

internal sealed class CliException(string message) : Exception(message);

internal sealed record RequestOptions(
    string Profile,
    string TenantId,
    int CallbackPort,
    int MetadataPort,
    int ExpiresMinutes,
    string? TunnelId,
    string AzdPath,
    string? GreenPostAuthScript)
{
    public const string MicrosoftTenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47";
}

internal sealed record ConnectOptions(
    string TunnelId,
    string RequestId,
    int MetadataPort,
    bool NoBrowser);
