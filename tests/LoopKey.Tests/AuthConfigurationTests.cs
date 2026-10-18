namespace LoopKey.Tests;

public class AuthConfigurationTests
{
    [Fact]
    public void LoadsGenericProfileAndExpandsEnvironment()
    {
        const string variable = "LOOPKEY_TEST_AUTH_ROOT";
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, @"C:\Auth Root");
        var path = WriteConfiguration(
            """
            {
              "profiles": {
                "custom": {
                  "login": {
                    "command": "%LOOPKEY_TEST_AUTH_ROOT%\\login.cmd",
                    "arguments": ["--port={callbackPort}"],
                    "environment": { "MODE": "interactive" }
                  },
                  "verification": {
                    "command": "%LOOPKEY_TEST_AUTH_ROOT%\\verify.exe",
                    "arguments": ["status"],
                    "successOutputContains": "ready"
                  },
                  "authorization": {
                    "allowedHosts": ["identity.example.test"],
                    "redirectUriParameter": "return_to",
                    "redirectUriTemplate": "http://127.0.0.1:{callbackPort}",
                    "requiredQueryParameters": ["nonce"],
                    "requiredQueryValues": { "method": "S256" }
                  },
                  "postAuth": {
                    "command": "%LOOPKEY_TEST_AUTH_ROOT%\\after.cmd",
                    "arguments": ["sync"]
                  }
                }
              }
            }
            """);

        try
        {
            var profile = AuthConfiguration.Load(path, "CUSTOM");
            var login = profile.Login.WithCallbackPort(9123);

            Assert.Equal("custom", profile.Name);
            Assert.Equal(@"C:\Auth Root\login.cmd", login.FileName);
            Assert.Equal("--port=9123", Assert.Single(login.Arguments));
            Assert.Equal("identity.example.test", Assert.Single(profile.Authorization.AllowedHosts));
            Assert.Equal("return_to", profile.Authorization.RedirectUriParameter);
            Assert.Equal(
                "http://127.0.0.1:{callbackPort}",
                profile.Authorization.RedirectUriTemplate);
            Assert.Equal(@"C:\Auth Root\after.cmd", profile.PostAuth?.FileName);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
            File.Delete(path);
        }
    }

    [Fact]
    public void RejectsLoginWithoutCallbackPortTemplate()
    {
        var path = WriteConfiguration(CreateMinimalConfiguration("[]"));
        try
        {
            var exception = Assert.Throws<CliException>(
                () => AuthConfiguration.Load(path, "custom"));

            Assert.Contains("{callbackPort}", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RejectsUnknownConfigurationFields()
    {
        var path = WriteConfiguration(
            CreateMinimalConfiguration("[\"{callbackPort}\"]")
                .Replace(
                    "\"profiles\":",
                    "\"unexpected\": true, \"profiles\":",
                    StringComparison.Ordinal));
        try
        {
            var exception = Assert.Throws<CliException>(
                () => AuthConfiguration.Load(path, "custom"));

            Assert.Contains("Invalid configuration", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ValidatesCommandsFromPath()
    {
        var path = WriteConfiguration(
            CreateMinimalConfiguration("[\"{callbackPort}\"]")
                .Replace("\"login.exe\"", "\"cmd.exe\"", StringComparison.Ordinal)
                .Replace("\"verify.exe\"", "\"cmd.exe\"", StringComparison.Ordinal));
        try
        {
            var profile = AuthConfiguration.Load(path, "custom");

            AuthConfiguration.ValidateCommandAvailability(profile);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RejectsMissingConfiguredCommand()
    {
        var path = WriteConfiguration(CreateMinimalConfiguration("[\"{callbackPort}\"]"));
        try
        {
            var profile = AuthConfiguration.Load(path, "custom");
            var exception = Assert.Throws<CliException>(
                () => AuthConfiguration.ValidateCommandAvailability(profile));

            Assert.Contains("command was not found", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateMinimalConfiguration(string loginArguments) =>
        $$"""
        {
          "profiles": {
            "custom": {
              "login": {
                "command": "login.exe",
                "arguments": {{loginArguments}}
              },
              "verification": {
                "command": "verify.exe",
                "arguments": [],
                "successOutputContains": "ready"
              },
              "authorization": {
                "allowedHosts": ["identity.example.test"]
              }
            }
          }
        }
        """;

    private static string WriteConfiguration(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"loopkey-profile-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }
}
