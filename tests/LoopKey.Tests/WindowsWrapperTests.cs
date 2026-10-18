namespace LoopKey.Tests;

public class WindowsWrapperTests
{
    [Fact]
    public async Task StartsQuotedCmdWrapperWithoutLiteralBackslashes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"LoopKey Test {Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var wrapper = Path.Combine(directory, "fake auth.cmd");
        await File.WriteAllTextAsync(wrapper, "@echo off\r\necho %*\r\n");

        try
        {
            var result = await ProcessRunner.RunRawAsync(
                "cmd.exe",
                RequestCommand.BuildCmdArguments(wrapper, ["auth", "login", "--redirect-port", "8400"]),
                environment: null,
                CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("auth login --redirect-port 8400", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
