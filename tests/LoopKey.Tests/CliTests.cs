namespace LoopKey.Tests;

public class CliTests
{
    [Fact]
    public void RequestLoadsNamedProfileAndDefaultPorts()
    {
        var config = WriteConfiguration();
        try
        {
            var options = Cli.ParseRequestOptions(
                ["--profile", "work", "--config", config]);

            Assert.Equal("work", options.Profile.Name);
            Assert.Equal("fake-login.cmd", options.Profile.Login.FileName);
            Assert.Equal("verified", options.Profile.SuccessOutputContains);
            Assert.Equal("post-auth.exe", options.Profile.PostAuth?.FileName);
            Assert.Equal(8400, options.CallbackPort);
            Assert.Equal(8401, options.MetadataPort);
        }
        finally
        {
            File.Delete(config);
        }
    }

    [Fact]
    public void ReportsPackageVersion()
    {
        Assert.Equal("0.3.0-alpha.1", Cli.Version);
    }

    [Fact]
    public void RequestRejectsUnknownProfile()
    {
        var config = WriteConfiguration();
        try
        {
            var exception = Assert.Throws<CliException>(
                () => Cli.ParseRequestOptions(
                    ["--profile", "missing", "--config", config]));

            Assert.Contains("was not found", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(config);
        }
    }

    [Fact]
    public void ConnectParsesOneCommandContract()
    {
        var options = Cli.ParseConnectOptions(
            ["loopkey-jbtem-1234", "request-1234", "--metadata-port", "9001", "--no-browser"]);

        Assert.Equal("loopkey-jbtem-1234", options.TunnelId);
        Assert.Equal("request-1234", options.RequestId);
        Assert.Equal(9001, options.MetadataPort);
        Assert.True(options.NoBrowser);
    }

    private static string WriteConfiguration()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loopkey-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(
            path,
            """
            {
              "profiles": {
                "work": {
                  "login": {
                    "command": "fake-login.cmd",
                    "arguments": ["login", "--port", "{callbackPort}"],
                    "environment": {
                      "INTERACTIVE": "true"
                    }
                  },
                  "verification": {
                    "command": "fake-check.exe",
                    "arguments": ["check"],
                    "successOutputContains": "verified"
                  },
                  "authorization": {
                    "allowedHosts": ["auth.example.com"],
                    "redirectUriParameter": "redirect_uri",
                    "requiredQueryParameters": ["state"],
                    "requiredQueryValues": {
                      "code_challenge_method": "S256"
                    }
                  },
                  "postAuth": {
                    "command": "post-auth.exe",
                    "arguments": []
                  }
                }
              }
            }
            """);
        return path;
    }
}
