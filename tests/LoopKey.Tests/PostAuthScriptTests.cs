namespace LoopKey.Tests;

public class PostAuthScriptTests
{
    [Fact]
    public async Task RunsCmdPostAuthScript()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"LoopKey Hook {Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "completed.txt");
        var script = Path.Combine(directory, "refresh.cmd");
        await File.WriteAllTextAsync(script, $"@echo off\r\necho refreshed>\"{marker}\"\r\n");

        try
        {
            var result = await RequestCommand.RunPostAuthScriptAsync(script, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(marker));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsUnsupportedPostAuthScript()
    {
        var exception = await Assert.ThrowsAsync<CliException>(
            () => RequestCommand.RunPostAuthScriptAsync(
                @"C:\tools\refresh.txt",
                CancellationToken.None));

        Assert.Contains(".ps1", exception.Message, StringComparison.Ordinal);
    }
}
