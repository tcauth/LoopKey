using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;

namespace LoopKey;

internal sealed class AuthConfigurationDocument
{
    public Dictionary<string, AuthProfileDefinition?>? Profiles { get; init; } = [];
}

internal sealed class AuthProfileDefinition
{
    public CommandDefinition? Login { get; init; }
    public VerificationDefinition? Verification { get; init; }
    public AuthorizationPolicyDefinition? Authorization { get; init; }
    public CommandDefinition? PostAuth { get; init; }
}

internal class CommandDefinition
{
    public string Command { get; init; } = "";
    public string[] Arguments { get; init; } = [];
    public Dictionary<string, string?> Environment { get; init; } = [];
}

internal sealed class VerificationDefinition : CommandDefinition
{
    public string SuccessOutputContains { get; init; } = "";
}

internal sealed class AuthorizationPolicyDefinition
{
    public string[] AllowedHosts { get; init; } = [];
    public string RedirectUriParameter { get; init; } = "redirect_uri";
    public string RedirectUriTemplate { get; init; } = "http://localhost:{callbackPort}";
    public string[] RequiredQueryParameters { get; init; } = ["state"];
    public Dictionary<string, string> RequiredQueryValues { get; init; } = [];
}

internal sealed record ConfiguredCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?> Environment)
{
    public ConfiguredCommand WithCallbackPort(int callbackPort) =>
        this with
        {
            Arguments = Arguments
                .Select(argument => argument.Replace(
                    "{callbackPort}",
                    callbackPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    StringComparison.Ordinal))
                .ToArray(),
        };
}

internal sealed record AuthorizationPolicy(
    IReadOnlyList<string> AllowedHosts,
    string RedirectUriParameter,
    string RedirectUriTemplate,
    IReadOnlyList<string> RequiredQueryParameters,
    IReadOnlyDictionary<string, string> RequiredQueryValues);

internal sealed record AuthProfile(
    string Name,
    ConfiguredCommand Login,
    ConfiguredCommand Verification,
    string SuccessOutputContains,
    AuthorizationPolicy Authorization,
    ConfiguredCommand? PostAuth);

internal static class AuthConfiguration
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static AuthProfile Load(string path, string profileName)
    {
        var resolvedPath = Environment.ExpandEnvironmentVariables(path);
        var document = LoadDocument(resolvedPath);
        var profiles = document.Profiles
            ?? throw new CliException(
                $"Configuration file '{resolvedPath}' requires a profiles object.");
        ValidateProfileNames(profiles, resolvedPath);
        var profileEntry = profiles.FirstOrDefault(
            pair => pair.Key.Equals(profileName, StringComparison.OrdinalIgnoreCase));
        if (profileEntry.Key is null)
        {
            throw new CliException(
                $"Profile '{profileName}' was not found in configuration file '{resolvedPath}'.");
        }

        return ResolveProfile(profileEntry.Key, profileEntry.Value);
    }

    public static IReadOnlyList<AuthProfile> LoadAll(string path)
    {
        var resolvedPath = Environment.ExpandEnvironmentVariables(path);
        var document = LoadDocument(resolvedPath);
        var profiles = document.Profiles
            ?? throw new CliException(
                $"Configuration file '{resolvedPath}' requires a profiles object.");
        ValidateProfileNames(profiles, resolvedPath);
        if (profiles.Count == 0)
        {
            throw new CliException(
                $"Configuration file '{resolvedPath}' must contain at least one profile.");
        }

        return profiles
            .Select(pair => ResolveProfile(pair.Key, pair.Value))
            .ToArray();
    }

    public static void ValidateCommandAvailability(AuthProfile profile)
    {
        EnsureCommandAvailable(profile.Login, profile.Name, "login");
        EnsureCommandAvailable(profile.Verification, profile.Name, "verification");
        if (profile.PostAuth is not null)
        {
            EnsureCommandAvailable(profile.PostAuth, profile.Name, "postAuth");
        }
    }

    private static AuthConfigurationDocument LoadDocument(string resolvedPath)
    {
        if (!File.Exists(resolvedPath))
        {
            throw new CliException($"Configuration file not found: {resolvedPath}");
        }

        AuthConfigurationDocument document;
        try
        {
            document = JsonSerializer.Deserialize<AuthConfigurationDocument>(
                File.ReadAllText(resolvedPath),
                JsonOptions)
                ?? throw new CliException($"Configuration file is empty: {resolvedPath}");
        }
        catch (JsonException exception)
        {
            throw new CliException($"Invalid configuration file '{resolvedPath}': {exception.Message}");
        }

        return document;
    }

    private static AuthProfile ResolveProfile(
        string profileName,
        AuthProfileDefinition? definition)
    {
        if (definition is null)
        {
            throw new CliException($"Profile '{profileName}' must be an object.");
        }

        var login = ResolveCommand(definition.Login, profileName, "login");
        if (!login.Arguments.Any(argument => argument.Contains("{callbackPort}", StringComparison.Ordinal)))
        {
            throw new CliException(
                $"Profile '{profileName}' login arguments must contain {{callbackPort}}.");
        }

        var verificationDefinition = definition.Verification
            ?? throw new CliException($"Profile '{profileName}' requires a verification command.");
        var verification = ResolveCommand(verificationDefinition, profileName, "verification");
        if (string.IsNullOrWhiteSpace(verificationDefinition.SuccessOutputContains))
        {
            throw new CliException(
                $"Profile '{profileName}' verification.successOutputContains is required.");
        }

        var authorization = ResolveAuthorization(definition.Authorization, profileName);
        var postAuth = definition.PostAuth is null
            ? null
            : ResolveCommand(definition.PostAuth, profileName, "postAuth");

        return new AuthProfile(
            profileName,
            login,
            verification,
            verificationDefinition.SuccessOutputContains,
            authorization,
            postAuth);
    }

    private static ConfiguredCommand ResolveCommand(
        CommandDefinition? definition,
        string profileName,
        string commandName)
    {
        if (definition is null || string.IsNullOrWhiteSpace(definition.Command))
        {
            throw new CliException($"Profile '{profileName}' requires a {commandName} command.");
        }

        var command = Environment.ExpandEnvironmentVariables(definition.Command);
        var configuredArguments = definition.Arguments
                ?? throw new CliException(
                    $"Profile '{profileName}' {commandName}.arguments must be an array.");
        if (configuredArguments.Any(argument => argument is null))
        {
            throw new CliException(
                $"Profile '{profileName}' {commandName}.arguments contains a null value.");
        }

        var arguments = configuredArguments
            .Select(Environment.ExpandEnvironmentVariables)
            .ToArray();
        var configuredEnvironment = definition.Environment
                ?? throw new CliException(
                    $"Profile '{profileName}' {commandName}.environment must be an object.");
        if (configuredEnvironment.Keys.Any(
            key => string.IsNullOrWhiteSpace(key) || key.Contains('=')))
        {
            throw new CliException(
                $"Profile '{profileName}' {commandName}.environment contains an invalid variable name.");
        }

        var environment = configuredEnvironment
            .ToDictionary(
            pair => pair.Key,
            pair => pair.Value is null
                ? null
                : Environment.ExpandEnvironmentVariables(pair.Value),
            StringComparer.OrdinalIgnoreCase);

        return new ConfiguredCommand(command, arguments, environment);
    }

    private static AuthorizationPolicy ResolveAuthorization(
        AuthorizationPolicyDefinition? definition,
        string profileName)
    {
        if (definition is null
            || definition.AllowedHosts is null
            || definition.AllowedHosts.Length == 0)
        {
            throw new CliException(
                $"Profile '{profileName}' authorization.allowedHosts must contain at least one host.");
        }
        if (definition.AllowedHosts.Any(
            host => string.IsNullOrWhiteSpace(host)
                || Uri.CheckHostName(host) == UriHostNameType.Unknown))
        {
            throw new CliException(
                $"Profile '{profileName}' authorization.allowedHosts contains an invalid host.");
        }
        if (string.IsNullOrWhiteSpace(definition.RedirectUriParameter))
        {
            throw new CliException(
                $"Profile '{profileName}' authorization.redirectUriParameter is required.");
        }
        if (string.IsNullOrWhiteSpace(definition.RedirectUriTemplate)
            || !definition.RedirectUriTemplate.Contains(
                "{callbackPort}",
                StringComparison.Ordinal))
        {
            throw new CliException(
                $"Profile '{profileName}' authorization.redirectUriTemplate must contain {{callbackPort}}.");
        }
        var exampleRedirect = definition.RedirectUriTemplate.Replace(
            "{callbackPort}",
            "1",
            StringComparison.Ordinal);
        if (!Uri.TryCreate(exampleRedirect, UriKind.Absolute, out var redirectUri)
            || !redirectUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || !IPAddress.TryParse(redirectUri.Host, out var redirectAddress)
                && !redirectUri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || redirectAddress is not null && !IPAddress.IsLoopback(redirectAddress))
        {
            throw new CliException(
                $"Profile '{profileName}' authorization.redirectUriTemplate must be an HTTP loopback URI.");
        }
        if (definition.RequiredQueryParameters is null)
        {
            throw new CliException(
                $"Profile '{profileName}' authorization.requiredQueryParameters must be an array.");
        }
        if (definition.RequiredQueryParameters.Any(string.IsNullOrWhiteSpace))
        {
            throw new CliException(
                $"Profile '{profileName}' authorization.requiredQueryParameters contains an empty name.");
        }
        if (definition.RequiredQueryValues is null)
        {
            throw new CliException(
                $"Profile '{profileName}' authorization.requiredQueryValues must be an object.");
        }
        if (definition.RequiredQueryValues.Any(
            pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)))
        {
            throw new CliException(
                $"Profile '{profileName}' authorization.requiredQueryValues contains an empty name or value.");
        }

        return new AuthorizationPolicy(
            definition.AllowedHosts.Select(host => host.ToLowerInvariant()).ToArray(),
            definition.RedirectUriParameter,
            definition.RedirectUriTemplate,
            definition.RequiredQueryParameters,
            new Dictionary<string, string>(
                definition.RequiredQueryValues,
                StringComparer.Ordinal));
    }

    private static void EnsureCommandAvailable(
        ConfiguredCommand command,
        string profileName,
        string commandName)
    {
        if (ResolveCommandPath(command.FileName) is null)
        {
            throw new CliException(
                $"Profile '{profileName}' {commandName} command was not found: {command.FileName}");
        }
    }

    private static string? ResolveCommandPath(string command)
    {
        if (Path.IsPathRooted(command) || command.Contains(Path.DirectorySeparatorChar))
        {
            return File.Exists(command) ? command : null;
        }

        var extensions = Path.HasExtension(command)
            ? [""]
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, command + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static void ValidateProfileNames(
        IReadOnlyDictionary<string, AuthProfileDefinition?> profiles,
        string resolvedPath)
    {
        if (profiles.Keys.Any(string.IsNullOrWhiteSpace))
        {
            throw new CliException(
                $"Configuration file '{resolvedPath}' contains an empty profile name.");
        }

        var duplicate = profiles.Keys
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new CliException(
                $"Configuration file '{resolvedPath}' contains case-insensitive duplicate profile '{duplicate.Key}'.");
        }
    }
}
