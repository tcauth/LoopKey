namespace LoopKey.Tests;

public class BrowserCaptureTests
{
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

        var result = BrowserCapture.ExtractAuthorizationUrl(commandLine);

        Assert.Equal(AuthorizationUrl, result);
    }

    [Fact]
    public void AcceptsExpectedLoopbackAndPkceParameters()
    {
        BrowserCapture.ValidateAuthorizationUrl(AuthorizationUrl, 8400);
    }

    [Fact]
    public void RejectsUnexpectedCallbackPort()
    {
        var exception = Assert.Throws<CliException>(
            () => BrowserCapture.ValidateAuthorizationUrl(AuthorizationUrl, 8402));

        Assert.Contains("8402", exception.Message, StringComparison.Ordinal);
    }
}
