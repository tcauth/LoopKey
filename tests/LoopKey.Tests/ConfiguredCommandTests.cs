namespace LoopKey.Tests;

public class ConfiguredCommandTests
{
    [Fact]
    public async Task RunsGenericCommandWithConfiguredEnvironment()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var command = new ConfiguredCommand(
            "cmd.exe",
            ["/d", "/s", "/c", "echo %LOOPKEY_COMMAND_TEST%"],
            new Dictionary<string, string?>
            {
                ["LOOPKEY_COMMAND_TEST"] = "configured",
            });

        var result = await RequestCommand.RunConfiguredCommandAsync(
            command,
            environmentOverrides: null,
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("configured", result.StandardOutput, StringComparison.Ordinal);
    }
}
