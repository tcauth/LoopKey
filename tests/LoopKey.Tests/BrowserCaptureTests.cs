namespace LoopKey.Tests;

public class BrowserCaptureTests
{
    private static readonly AuthorizationPolicy Policy = new(
        ["login.microsoftonline.com"],
        "redirect_uri",
        "http://localhost:{callbackPort}",
        ["state", "code_challenge"],
        new Dictionary<string, string> { ["code_challenge_method"] = "S256" });

    private const string AuthorizationUrl =
        "https://login.microsoftonline.com/tenant/oauth2/v2.0/authorize"
        + "?client_id=client"
        + "&code_challenge=challenge"
        + "&code_challenge_method=S256"
        + "&redirect_uri=http%3A%2F%2Flocalhost%3A8400"
        + "&response_type=code"
        + "&state=state";

    [Fact]
    public void ExtractsCompleteUrlFromCmdCommandLine()
    {
        var commandLine = $"cmd.exe /c capture-browser.cmd {AuthorizationUrl}";

        var result = BrowserCapture.ExtractAuthorizationUrl(
            commandLine,
            Policy.AllowedHosts);

        Assert.Equal(AuthorizationUrl, result);
    }

    [Fact]
    public void AcceptsExpectedLoopbackAndPkceParameters()
    {
        BrowserCapture.ValidateAuthorizationUrl(AuthorizationUrl, 8400, Policy);
    }

    [Fact]
    public void RejectsUnexpectedCallbackPort()
    {
        var exception = Assert.Throws<CliException>(
            () => BrowserCapture.ValidateAuthorizationUrl(AuthorizationUrl, 8402, Policy));

        Assert.Contains("8402", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsHostOutsideProfilePolicy()
    {
        var exception = Assert.Throws<CliException>(
            () => BrowserCapture.ExtractAuthorizationUrl(
                AuthorizationUrl,
                ["auth.example.com"]));

        Assert.Contains("allowed host", exception.Message, StringComparison.Ordinal);
    }
}
