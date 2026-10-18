namespace LoopKey.Tests;

public class CliTests
{
    [Fact]
    public void CorpRequestUsesMicrosoftTenantAndDefaultPorts()
    {
        var options = Cli.ParseRequestOptions(["--profile", "corp"]);

        Assert.Equal("corp", options.Profile);
        Assert.Equal(RequestOptions.MicrosoftTenantId, options.TenantId);
        Assert.Equal(8400, options.CallbackPort);
        Assert.Equal(8401, options.MetadataPort);
        Assert.EndsWith(
            Path.Combine(".azd-profiles", "azd-corp.cmd"),
            options.AzdPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(options.GreenPostAuthScript);
    }

    [Fact]
    public void GreenRequestAcceptsPostAuthScript()
    {
        var options = Cli.ParseRequestOptions(
            [
                "--profile", "green",
                "--tenant-id", "green-tenant",
                "--green-post-auth-script", @"C:\tools\refresh.py",
            ]);

        Assert.Equal(@"C:\tools\refresh.py", options.GreenPostAuthScript);
    }

    [Fact]
    public void CorpRequestRejectsGreenPostAuthScript()
    {
        var exception = Assert.Throws<CliException>(
            () => Cli.ParseRequestOptions(
                [
                    "--profile", "corp",
                    "--green-post-auth-script", @"C:\tools\refresh.py",
                ]));

        Assert.Contains("only with the green profile", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsPackageVersion()
    {
        Assert.Equal("0.2.0-alpha.1", Cli.Version);
    }

    [Fact]
    public void GreenRequestRequiresTenant()
    {
        var previous = Environment.GetEnvironmentVariable("LOOPKEY_GREEN_TENANT_ID");
        Environment.SetEnvironmentVariable("LOOPKEY_GREEN_TENANT_ID", null);
        try
        {
            var exception = Assert.Throws<CliException>(
                () => Cli.ParseRequestOptions(["--profile", "green"]));

            Assert.Contains("--tenant-id", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOOPKEY_GREEN_TENANT_ID", previous);
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
}
